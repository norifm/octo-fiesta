using System.Text.Json;

namespace octo_fiesta.Models.Remote;

// The JSON nori's remote control and jams speak (nori crates/remote wire.rs). The hub reads only the
// routing fields; states and event bodies pass through untouched.

public sealed record RemoteMember(string Id, string Name, string Kind, JsonElement? State);

public sealed record RemoteRoom(string Room, bool Jam, IReadOnlyList<RemoteMember> Members);

public sealed record RemoteEvent(long Seq, string Room, string From, JsonElement Body);

/// <summary>
/// <paramref name="Along"/>: jam members may stream the songs of their host's queue (listening along).
/// <paramref name="Time"/>: the server tells its time at <c>nori/time</c> (<see cref="Services.Remote.RemoteTimeController"/>).
/// </summary>
public sealed record RemoteAnswer(long Seq, string You, IReadOnlyList<RemoteRoom> Rooms, IReadOnlyList<RemoteEvent> Events, bool Along = true, bool Time = true);

/// <summary>What a device sends: its state, an event, or both. A null room is the account's.</summary>
public sealed class RemoteOutgoing
{
    public string? Room { get; set; }
    public string? To { get; set; }
    public JsonElement? State { get; set; }
    public JsonElement? Body { get; set; }
}

/// <summary>Who a request is from: an account's device, or a jam member through its key.</summary>
public sealed record RemoteCaller(string? User, string? GuestRoom, string? GuestMember)
{
    public static RemoteCaller Account(string user) => new(user, null, null);
    public static RemoteCaller Guest(string room, string member) => new(null, room, member);
}

/// <summary>A jam key: an invite (no member yet) or a member's own.</summary>
public sealed record RemoteKey(string Room, string? Member);
