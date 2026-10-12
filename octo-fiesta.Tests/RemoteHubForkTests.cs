using System.Text.Json;
using Microsoft.AspNetCore.Http;
using octo_fiesta.Models.Remote;
using octo_fiesta.Services.Remote;

namespace octo_fiesta.Tests;

public class RemoteHubForkTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly RemoteHub _hub;

    public RemoteHubForkTests() => _hub = new RemoteHub(() => _now);

    private static readonly RemoteCaller Ann = RemoteCaller.Account("ann");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<RemoteAnswer> Poll(RemoteCaller caller, string dev, bool serve = false, long? since = null, bool hold = false, CancellationToken ct = default) =>
        _hub.PollAsync(caller, dev, dev.ToUpperInvariant(), "phone", serve, since, hold, ct);

    private long? Send(RemoteCaller caller, string dev, string? room = null, string? to = null, string? state = null, string? body = null) =>
        _hub.Send(caller, dev, dev.ToUpperInvariant(), "desktop", new RemoteOutgoing
        {
            Room = room,
            To = to,
            State = state == null ? null : Json(state),
            Body = body == null ? null : Json(body),
        });

    [Fact]
    public async Task ServingDevicesAreListedWithTheirState()
    {
        await Poll(Ann, "phone", serve: true);
        Send(Ann, "phone", state: """{"playing":true}""");
        await Poll(Ann, "desk");

        var answer = await Poll(Ann, "desk");
        var members = answer.Rooms.Single().Members;
        Assert.Equal(["phone"], members.Select(m => m.Id));
        Assert.True(members[0].State!.Value.GetProperty("playing").GetBoolean());

        var bob = await Poll(RemoteCaller.Account("bob"), "tv");
        Assert.Empty(bob.Rooms.Single().Members);
    }

    [Fact]
    public async Task AHeldPollReturnsWithTheEventForIt()
    {
        var start = await Poll(Ann, "phone", serve: true);
        var held = Poll(Ann, "phone", serve: true, since: start.Seq, hold: true);
        Assert.False(held.IsCompleted);

        Send(Ann, "desk", to: "tv", body: """{"t":"command","id":1,"op":{"op":"play"}}""");
        Send(Ann, "desk", to: "phone", body: """{"t":"command","id":2,"op":{"op":"pause"}}""");
        var answer = await held.WaitAsync(TimeSpan.FromSeconds(5));
        var all = (await Poll(Ann, "phone", serve: true, since: start.Seq)).Events;

        Assert.Equal([2L], all.Select(e => e.Body.GetProperty("id").GetInt64()));
        Assert.Equal("desk", all[0].From);
        Assert.True(answer.Seq > start.Seq);
    }

    [Fact]
    public async Task AHeldPollEndsWhenTheCallerGoes()
    {
        var start = await Poll(Ann, "phone", serve: true);
        using var gone = new CancellationTokenSource();
        var held = Poll(Ann, "phone", serve: true, since: start.Seq, hold: true, ct: gone.Token);
        gone.Cancel();
        var answer = await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(answer.Events);
    }

    [Fact]
    public async Task AHeldPollIsNotEndedByItsOwnSends()
    {
        var start = await Poll(Ann, "phone", serve: true);
        var held = Poll(Ann, "phone", serve: true, since: start.Seq, hold: true);

        Send(Ann, "phone", state: """{"playing":true}""");
        Send(Ann, "phone", to: "desk", body: """{"t":"ack","id":1}""");
        await Task.Delay(200);
        Assert.False(held.IsCompleted);

        Send(Ann, "desk", state: """{"playing":false}""");
        var answer = await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(answer.Rooms.Single().Members, m => m.Id == "desk");
    }

    [Fact]
    public async Task DevicesLeaveTheListSoonAfterTheirLastPoll()
    {
        // The phone stopped polling; the tablet holds a poll all the while.
        await Poll(Ann, "phone", serve: true);
        var tablet = await Poll(Ann, "tablet", serve: true);
        var held = Poll(Ann, "tablet", serve: true, since: tablet.Seq, hold: true);
        _now += TimeSpan.FromSeconds(30);

        var answer = await Poll(Ann, "desk");
        Assert.Equal(["tablet"], answer.Rooms.Single().Members.Select(m => m.Id));
        // The phone leaving answers the tablet's poll; it polls no more.
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        _now += TimeSpan.FromSeconds(30);
        Assert.Empty((await Poll(Ann, "desk")).Rooms.Single().Members);
    }

    [Fact]
    public async Task AJamTakesGuestsThroughItsInvite()
    {
        var (room, invite) = _hub.Open("ann", "phone", "Ann's phone", [new("u", "ann"), new("t", "tok"), new("s", "salt")]);
        Assert.Null(_hub.Join("wrong", "Eve"));
        var gus = _hub.Join(invite, "Gus")!.Value;
        Assert.Equal(room, gus.Room);

        var found = _hub.FindKey(gus.Key)!.Value;
        Assert.Equal(new RemoteKey(room, gus.Member), found.Key);
        Assert.Equal(["u", "t", "s"], found.HostAuth.Select(a => a.Key));

        var guest = RemoteCaller.Guest(room, gus.Member);
        Assert.Null(Send(guest, "", state: """{"playing":true}"""));
        Assert.NotNull(Send(guest, "", to: "phone", body: """{"t":"command","id":1,"op":{"op":"request"}}"""));
        Assert.Null(Send(RemoteCaller.Account("bob"), "tv", room: room, state: "{}"));
        Assert.NotNull(Send(Ann, "phone", room: room, state: """{"jam":{}}"""));

        var host = await Poll(Ann, "phone", since: 0);
        var jam = host.Rooms.Single(r => r.Jam);
        Assert.Equal(["phone", gus.Member], jam.Members.Select(m => m.Id));
        Assert.Equal([gus.Member], host.Events.Select(e => e.From));

        var seen = await Poll(guest, "");
        Assert.Equal(gus.Member, seen.You);
        Assert.Equal([room], seen.Rooms.Select(r => r.Room));

        Assert.False(_hub.Kick("bob", room, gus.Member));
        Assert.True(_hub.Kick("ann", room, gus.Member));
        Assert.Null(_hub.FindKey(gus.Key));

        Assert.True(_hub.Close("ann", room));
        Assert.Null(_hub.FindKey(invite));
    }

    [Fact]
    public void AJamEndsWhenItsHostGoesQuiet()
    {
        var (_, invite) = _hub.Open("ann", "phone", "Ann", []);
        _now += TimeSpan.FromMinutes(31);
        _hub.Send(Ann, "desk", "Desk", "desktop", new RemoteOutgoing());
        _ = Poll(Ann, "desk");
        Assert.Null(_hub.FindKey(invite));
    }
}

public class JamGuestMiddlewareForkTests
{
    private readonly RemoteHub _hub = new();

    private async Task<(HttpContext Context, bool Passed)> Run(string path, string query)
    {
        var passed = false;
        var middleware = new JamGuestMiddleware(_ => { passed = true; return Task.CompletedTask; }, _hub);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return (context, passed);
    }

    [Fact]
    public async Task AGuestSearchesWithTheHostsRights()
    {
        var (_, invite) = _hub.Open("ann", "phone", "Ann", [new("u", "ann"), new("t", "tok"), new("s", "salt")]);
        var gus = _hub.Join(invite, "Gus")!.Value;

        var (context, passed) = await Run("/rest/search3.view", $"?apiKey=nori-jam-{gus.Key}&query=dogs&f=json&u=evil");
        Assert.True(passed);
        var q = context.Request.Query;
        Assert.Equal(("ann", "tok", "salt", "dogs"), (q["u"].ToString(), q["t"].ToString(), q["s"].ToString(), q["query"].ToString()));
        Assert.False(q.ContainsKey("apiKey"));
        Assert.Equal(new RemoteKey(gus.Room, gus.Member), context.Items[JamGuestMiddleware.CallerItem]);
    }

    [Theory]
    [InlineData("/rest/getAlbumList2.view")]
    [InlineData("/rest/getArtists")]
    [InlineData("/rest/getArtistInfo2")]
    [InlineData("/rest/getTopSongs")]
    [InlineData("/rest/getGenres")]
    [InlineData("/rest/getSongsByGenre")]
    [InlineData("/rest/getRandomSongs")]
    [InlineData("/rest/getSimilarSongs2")]
    [InlineData("/rest/getLyricsBySongId")]
    public async Task AGuestBrowsesTheHostsLibrary(string path)
    {
        var (_, invite) = _hub.Open("ann", "phone", "Ann", [new("u", "ann"), new("p", "pw")]);
        var gus = _hub.Join(invite, "Gus")!.Value;
        var (context, passed) = await Run(path, $"?apiKey=nori-jam-{gus.Key}&id=ar-1");
        Assert.True(passed);
        Assert.Equal("ann", context.Request.Query["u"].ToString());
    }

    [Theory]
    [InlineData("/rest/stream")]
    [InlineData("/rest/download")]
    [InlineData("/rest/star")]
    [InlineData("/rest/unstar")]
    [InlineData("/rest/setRating")]
    [InlineData("/rest/scrobble")]
    [InlineData("/rest/createPlaylist")]
    [InlineData("/rest/updatePlaylist")]
    [InlineData("/rest/deletePlaylist")]
    [InlineData("/rest/getPlaylists")]
    [InlineData("/rest/getPlaylist")]
    [InlineData("/rest/getStarred2")]
    [InlineData("/rest/savePlayQueue")]
    [InlineData("/rest/getPlayQueue")]
    [InlineData("/rest/createShare")]
    [InlineData("/rest/createInternetRadioStation")]
    [InlineData("/rest/startScan")]
    [InlineData("/rest/noriRemote.open")]
    public async Task AGuestCannotDoMore(string path)
    {
        var (_, invite) = _hub.Open("ann", "phone", "Ann", [new("u", "ann"), new("p", "pw")]);
        var gus = _hub.Join(invite, "Gus")!.Value;
        var (context, passed) = await Run(path, $"?apiKey=nori-jam-{gus.Key}&id=ext-deezer-song-1");
        Assert.False(passed);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task AGuestStreamsOnlyTheHostsQueueWhileItLetsThem()
    {
        var (room, invite) = _hub.Open("ann", "phone", "Ann", [new("u", "ann"), new("p", "pw")]);
        var gus = _hub.Join(invite, "Gus")!.Value;
        var ann = RemoteCaller.Account("ann");
        void Host(string along) => _hub.Send(ann, "phone", "Ann", "phone", new RemoteOutgoing
        {
            Room = room,
            State = JsonDocument.Parse(
                "{\"index\":4,\"entries\":[{\"index\":2,\"turn\":0,\"id\":\"s0\"},{\"index\":4,\"turn\":1,\"id\":\"s1\"},{\"index\":0,\"turn\":2,\"id\":\"ext-deezer-song-7\"}],"
                + "\"jam\":{\"pending\":[{\"song\":{\"id\":\"ext-deezer-song-9\"}}]" + along + "}}").RootElement.Clone(),
        });
        async Task<int> Stream(string id) => (await Run("/rest/stream", $"?apiKey=nori-jam-{gus.Key}&id={id}")).Context.Response.StatusCode;

        Host("");
        Assert.Equal(403, await Stream("s1"));
        Host(",\"along\":{\"speed\":1,\"pitch\":1}");
        var (context, passed) = await Run("/rest/stream.view", $"?apiKey=nori-jam-{gus.Key}&id=s1");
        Assert.True(passed);
        Assert.Equal("ann", context.Request.Query["u"].ToString());
        Assert.True((await Run("/rest/stream", $"?apiKey=nori-jam-{gus.Key}&id=ext-deezer-song-7")).Passed, "an accepted provider song");
        // Played already, asked for and not accepted, or not in the queue at all.
        foreach (var id in new[] { "s0", "ext-deezer-song-9", "s9" })
        {
            Assert.Equal(403, await Stream(id));
        }
        Assert.False((await Run("/rest/stream", $"?apiKey=nori-jam-{invite}&id=s1")).Passed, "an invite only joins");
    }

    [Fact]
    public async Task AnInviteOnlyJoins()
    {
        var (_, invite) = _hub.Open("ann", "phone", "Ann", [new("u", "ann"), new("p", "pw")]);
        Assert.False((await Run("/rest/search3", $"?apiKey=nori-jam-{invite}")).Passed);
        Assert.True((await Run("/rest/noriRemote.join", $"?apiKey=nori-jam-{invite}&name=Gus")).Passed);
        Assert.Equal(401, (await Run("/rest/search3", "?apiKey=nori-jam-unknown")).Context.Response.StatusCode);
    }

    [Fact]
    public async Task OtherRequestsPassUntouched()
    {
        var (context, passed) = await Run("/rest/search3", "?u=ann&t=x&s=y&apiKey=other");
        Assert.True(passed);
        Assert.Equal("?u=ann&t=x&s=y&apiKey=other", context.Request.QueryString.Value);
    }
}
