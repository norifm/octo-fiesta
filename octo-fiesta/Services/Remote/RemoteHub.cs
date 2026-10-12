using System.Security.Cryptography;
using System.Text.Json;
using octo_fiesta.Models.Remote;

namespace octo_fiesta.Services.Remote;

/// <summary>
/// The relay for nori's remote control and jams, in memory. Rooms hold members and events: each account
/// has its own room (its controllable devices), and a jam is a room its host opened, which guests join
/// with an invite key. A poll answers with the caller's rooms and the events for it after a sequence,
/// held up to <see cref="Hold"/> while nothing new happened in those rooms.
/// </summary>
public sealed class RemoteHub
{
    /// <summary>Under the 60 s read timeout of common reverse proxies.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(50);

    /// <summary>
    /// An account device with no poll held, not heard from for this long, is no longer listed. A device
    /// that serves polls again as soon as an answer comes, so this is how long it can be between polls.
    /// </summary>
    private static readonly TimeSpan Presence = TimeSpan.FromSeconds(20);

    /// <summary>A jam whose host is not heard from for this long ends.</summary>
    private static readonly TimeSpan JamIdle = TimeSpan.FromMinutes(30);

    private const int KeptEvents = 256;
    /// <summary>Changes kept per room, by whom: enough for a poll to tell its own sends from others'.</summary>
    private const int KeptChanges = 64;
    private const int MaxJamMembers = 32;

    private sealed class Member
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Kind { get; init; }
        public JsonElement? State { get; set; }
        public DateTime Seen { get; set; }
        /// <summary>Its polls waiting for news now: it is there, however long they wait.</summary>
        public int Holding { get; set; }
    }

    private sealed class Room
    {
        public required string Id { get; init; }
        public bool Jam { get; init; }
        public string? HostUser { get; init; }
        public string? HostDevice { get; init; }
        /// <summary>The host's Subsonic credentials, which a guest's requests are made with.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> HostAuth { get; init; } = [];
        public List<Member> Members { get; } = [];
        public LinkedList<(long Seq, string From, string? To, JsonElement Body)> Events { get; } = new();
        /// <summary>Its last changes and the member that made each (null: the hub).</summary>
        public LinkedList<(long Seq, string? By)> Changes { get; } = new();
    }

    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly Dictionary<string, RemoteKey> _keys = new();
    private long _seq;
    private TaskCompletionSource _changed = NewSignal();

    public RemoteHub() : this(() => DateTime.UtcNow) { }

    public RemoteHub(Func<DateTime> now) => _now = now;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string AccountRoom(string user) => "u:" + user;

    /// <summary>Marks <paramref name="room"/> changed by member <paramref name="by"/> (null: the hub) and wakes held polls; the new sequence.</summary>
    private long Touch(Room room, string? by = null)
    {
        room.Changes.AddLast((++_seq, by));
        while (room.Changes.Count > KeptChanges)
        {
            room.Changes.RemoveFirst();
        }
        var waiting = _changed;
        _changed = NewSignal();
        waiting.TrySetResult();
        return _seq;
    }

    private Room AccountRoomOf(string user)
    {
        var id = AccountRoom(user);
        if (!_rooms.TryGetValue(id, out var room))
        {
            room = new Room { Id = id };
            _rooms[id] = room;
        }
        return room;
    }

    private void Forget(Room room)
    {
        _rooms.Remove(room.Id);
        foreach (var key in _keys.Where(k => k.Value.Room == room.Id).Select(k => k.Key).ToList())
        {
            _keys.Remove(key);
        }
        Touch(room);
    }

    /// <summary>Drops account devices gone quiet and jams whose host has.</summary>
    private void Sweep()
    {
        var now = _now();
        foreach (var room in _rooms.Values.ToList())
        {
            if (room.Jam)
            {
                var host = room.Members.FirstOrDefault(m => m.Id == room.HostDevice);
                if (host == null || now - host.Seen > JamIdle)
                {
                    Forget(room);
                }
            }
            else if (room.Members.RemoveAll(m => m.Holding == 0 && now - m.Seen > Presence) > 0)
            {
                Touch(room);
            }
        }
    }

    private static RemoteMember View(Member m) => new(m.Id, m.Name, m.Kind, m.State);

    /// <summary>
    /// Whether <paramref name="room"/> changed after <paramref name="since"/> other than by <paramref name="me"/>:
    /// a device's own sends do not end its held poll.
    /// </summary>
    private static bool NewsFor(Room room, long since, string me)
    {
        // Changes after `since` were dropped from what is kept: they may be anyone's.
        if (room.Changes.Count == KeptChanges && room.Changes.First!.Value.Seq > since + 1)
        {
            return true;
        }
        return room.Changes.Any(c => c.Seq > since && c.By != me);
    }

    /// <summary>The caller's members in <paramref name="rooms"/>.</summary>
    private List<Member> MembersOf(string me, IEnumerable<string> rooms) =>
        rooms.Select(_rooms.GetValueOrDefault).OfType<Room>().SelectMany(r => r.Members).Where(m => m.Id == me).ToList();

    private RemoteAnswer Answer(string me, IReadOnlyList<string> rooms, long? since)
    {
        var present = rooms.Where(_rooms.ContainsKey).Select(r => _rooms[r]).ToList();
        var events = since is { } s
            ? present.SelectMany(r => r.Events
                    .Where(e => e.Seq > s && e.From != me && (e.To == null || e.To == me))
                    .Select(e => new RemoteEvent(e.Seq, r.Id, e.From, e.Body)))
                .OrderBy(e => e.Seq)
                .ToList()
            : [];
        return new RemoteAnswer(_seq, me, present.Select(r => new RemoteRoom(r.Id, r.Jam, r.Members.Select(View).ToList())).ToList(), events);
    }

    /// <summary>
    /// Lists the caller's rooms and its events after <paramref name="since"/>. With <paramref name="hold"/>,
    /// waits until one of its rooms changes, up to <see cref="Hold"/>. An account device is listed in its
    /// account's room while it polls with <paramref name="serve"/>.
    /// </summary>
    public async Task<RemoteAnswer> PollAsync(RemoteCaller caller, string dev, string name, string kind, bool serve, long? since, bool hold, CancellationToken ct)
    {
        string me;
        List<string> rooms;
        lock (_lock)
        {
            Sweep();
            (me, rooms) = Present(caller, dev, name, kind, serve);
        }
        var until = _now() + Hold;
        List<Member> holding = [];
        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    var news = since is not { } s || !hold || rooms.Any(r => !_rooms.TryGetValue(r, out var room) || NewsFor(room, s, me));
                    var left = until - _now();
                    if (news || left <= TimeSpan.Zero || ct.IsCancellationRequested)
                    {
                        Present(caller, dev, name, kind, serve);
                        return Answer(me, rooms, since);
                    }
                    if (holding.Count == 0)
                    {
                        holding = MembersOf(me, rooms);
                        holding.ForEach(m => m.Holding++);
                    }
                    changed = _changed.Task;
                }
                var left2 = until - _now();
                if (left2 > TimeSpan.Zero)
                {
                    await Task.WhenAny(changed, Task.Delay(left2, ct)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                holding.ForEach(m => m.Holding--);
            }
        }
    }

    /// <summary>Marks the caller seen; its member id and its rooms.</summary>
    private (string, List<string>) Present(RemoteCaller caller, string dev, string name, string kind, bool serve)
    {
        var now = _now();
        if (caller.User is { } user)
        {
            var account = AccountRoomOf(user);
            var listed = account.Members.Find(m => m.Id == dev);
            if (serve && listed == null)
            {
                account.Members.Add(new Member { Id = dev, Name = name, Kind = kind, Seen = now });
                Touch(account);
            }
            else if (!serve && listed != null)
            {
                account.Members.Remove(listed);
                Touch(account);
            }
            else if (listed != null)
            {
                listed.Seen = now;
            }
            var hosted = _rooms.Values.Where(r => r.Jam && r.HostUser == user && r.HostDevice == dev).ToList();
            foreach (var host in hosted.SelectMany(r => r.Members).Where(m => m.Id == dev))
            {
                host.Seen = now;
            }
            return (dev, [account.Id, .. hosted.Select(r => r.Id)]);
        }
        var guest = _rooms.GetValueOrDefault(caller.GuestRoom!)?.Members.Find(m => m.Id == caller.GuestMember);
        if (guest != null)
        {
            guest.Seen = now;
        }
        return (caller.GuestMember!, [caller.GuestRoom!]);
    }

    /// <summary>Publishes the sender's state and/or posts an event; the new sequence, or null when the caller may not.</summary>
    public long? Send(RemoteCaller caller, string dev, string name, string kind, RemoteOutgoing outgoing)
    {
        lock (_lock)
        {
            Room? room;
            string from;
            if (caller.User is { } user)
            {
                from = dev;
                if (outgoing.Room == null)
                {
                    room = AccountRoomOf(user);
                }
                else
                {
                    room = _rooms.GetValueOrDefault(outgoing.Room);
                    if (room is not { Jam: true } || room.HostUser != user || room.HostDevice != dev)
                    {
                        return null;
                    }
                }
            }
            else
            {
                if (outgoing.State != null || (outgoing.Room != null && outgoing.Room != caller.GuestRoom))
                {
                    return null;
                }
                room = _rooms.GetValueOrDefault(caller.GuestRoom!);
                from = caller.GuestMember!;
                if (room == null)
                {
                    return null;
                }
            }
            var member = room.Members.Find(m => m.Id == from);
            if (outgoing.State is { } state)
            {
                // A device publishing in its account's room is serving, polled or not yet.
                if (member == null && !room.Jam)
                {
                    member = new Member { Id = from, Name = name, Kind = kind };
                    room.Members.Add(member);
                }
                if (member == null)
                {
                    return null;
                }
                member.State = state.Clone();
            }
            if (member != null)
            {
                member.Seen = _now();
            }
            var seq = Touch(room, from);
            if (outgoing.Body is { } body)
            {
                room.Events.AddLast((seq, from, outgoing.To, body.Clone()));
                while (room.Events.Count > KeptEvents)
                {
                    room.Events.RemoveFirst();
                }
            }
            return seq;
        }
    }

    /// <summary>Opens a jam hosted by <paramref name="dev"/>, ending any it hosted before.</summary>
    public (string Room, string Invite) Open(string user, string dev, string name, IReadOnlyList<KeyValuePair<string, string>> hostAuth)
    {
        lock (_lock)
        {
            foreach (var old in _rooms.Values.Where(r => r.Jam && r.HostUser == user && r.HostDevice == dev).ToList())
            {
                Forget(old);
            }
            var room = new Room { Id = NewKey(), Jam = true, HostUser = user, HostDevice = dev, HostAuth = hostAuth };
            room.Members.Add(new Member { Id = dev, Name = name, Kind = "phone", Seen = _now() });
            _rooms[room.Id] = room;
            var invite = NewKey();
            _keys[invite] = new RemoteKey(room.Id, null);
            Touch(room);
            return (room.Id, invite);
        }
    }

    private Room? Hosted(string user, string roomId)
    {
        var room = _rooms.GetValueOrDefault(roomId);
        return room is { Jam: true } && room.HostUser == user ? room : null;
    }

    public bool Close(string user, string roomId)
    {
        lock (_lock)
        {
            var room = Hosted(user, roomId);
            if (room == null)
            {
                return false;
            }
            Forget(room);
            return true;
        }
    }

    /// <summary>Sends a member out of a jam: its key stops working.</summary>
    public bool Kick(string user, string roomId, string member)
    {
        lock (_lock)
        {
            var room = Hosted(user, roomId);
            return room != null && member != room.HostDevice && Remove(room, member);
        }
    }

    private bool Remove(Room room, string member)
    {
        if (room.Members.RemoveAll(m => m.Id == member) == 0)
        {
            return false;
        }
        foreach (var key in _keys.Where(k => k.Value.Room == room.Id && k.Value.Member == member).Select(k => k.Key).ToList())
        {
            _keys.Remove(key);
        }
        Touch(room);
        return true;
    }

    /// <summary>A guest joins with an invite key; its member id and its own key, or null.</summary>
    public (string Room, string Member, string Key)? Join(string invite, string name)
    {
        lock (_lock)
        {
            if (_keys.GetValueOrDefault(invite) is not { Member: null } k || _rooms.GetValueOrDefault(k.Room) is not { } room || room.Members.Count >= MaxJamMembers)
            {
                return null;
            }
            var member = "g" + NewKey()[..12];
            var key = NewKey();
            _keys[key] = new RemoteKey(room.Id, member);
            room.Members.Add(new Member { Id = member, Name = string.IsNullOrWhiteSpace(name) ? "Guest" : name.Trim(), Kind = "guest", Seen = _now() });
            Touch(room);
            return (room.Id, member, key);
        }
    }

    public bool Leave(string roomId, string member)
    {
        lock (_lock)
        {
            return _rooms.GetValueOrDefault(roomId) is { } room && member != room.HostDevice && Remove(room, member);
        }
    }

    /// <summary>
    /// Whether a member of jam <paramref name="roomId"/> may stream song <paramref name="id"/>: while the host lets
    /// its guests listen along, and only a song of its queue from the one playing on (the ones it plays, and
    /// requests once accepted; a provider song asked for and not accepted is not in it).
    /// </summary>
    public bool MayStream(string roomId, string id)
    {
        lock (_lock)
        {
            if (_rooms.GetValueOrDefault(roomId) is not { Jam: true } room
                || room.Members.Find(m => m.Id == room.HostDevice)?.State is not { } state)
            {
                return false;
            }
            return Queued(state, id);
        }
    }

    /// <summary>Song <paramref name="id"/> is in the queue a host's state lists, at or after the song playing, and the host lets guests listen along.</summary>
    private static bool Queued(JsonElement state, string id)
    {
        if (state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("jam", out var jam) || jam.ValueKind != JsonValueKind.Object
            || !jam.TryGetProperty("along", out var along) || along.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number
            || !state.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var listed = entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;
        var playing = listed.Find(e => Number(e, "index") == index.GetInt64());
        if (playing.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var from = Number(playing, "turn");
        return listed.Any(e => Number(e, "turn") >= from && e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == id);
    }

    /// <summary>What a jam key opens, with the host's credentials its requests are made with.</summary>
    public (RemoteKey Key, IReadOnlyList<KeyValuePair<string, string>> HostAuth)? FindKey(string key)
    {
        lock (_lock)
        {
            if (_keys.GetValueOrDefault(key) is not { } k || _rooms.GetValueOrDefault(k.Room) is not { } room)
            {
                return null;
            }
            return (k, room.HostAuth);
        }
    }
}
