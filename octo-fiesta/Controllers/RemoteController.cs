using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using octo_fiesta.Models.Remote;
using octo_fiesta.Services.Remote;

namespace octo_fiesta.Controllers;

/// <summary>
/// nori's remote control and jam relay (<see cref="RemoteHub"/>). Account callers come through the
/// Subsonic authentication, so their <c>u</c> is checked; jam guests through <see cref="JamGuestMiddleware"/>.
/// Answers are plain JSON.
/// </summary>
[ApiController]
public sealed class RemoteController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AuthParams = ["u", "t", "s", "p"];

    private readonly RemoteHub _hub;

    public RemoteController(RemoteHub hub) => _hub = hub;

    private string Param(string name) => Request.Query[name].ToString();

    private RemoteCaller? Caller() => HttpContext.Items[JamGuestMiddleware.CallerItem] switch
    {
        RemoteKey { Member: { } member } key => RemoteCaller.Guest(key.Room, member),
        RemoteKey => null,
        _ when Param("u") is { Length: > 0 } user => RemoteCaller.Account(user),
        _ => null,
    };

    private ContentResult Answer(object value) => Content(JsonSerializer.Serialize(value, Json), "application/json");

    private static ObjectResult Refused(string why) => new(new { error = why }) { StatusCode = 403 };

    [HttpGet, Route("rest/noriRemote.poll"), Route("rest/noriRemote.poll.view")]
    public async Task<IActionResult> Poll()
    {
        if (Caller() is not { } caller)
        {
            return Refused("no caller");
        }
        long? since = long.TryParse(Param("since"), out var s) ? s : null;
        var answer = await _hub.PollAsync(caller, Param("dev"), Param("name"), Param("kind"), Param("serve") == "1", since, Param("hold") == "1", HttpContext.RequestAborted);
        return Answer(answer);
    }

    [HttpPost, Route("rest/noriRemote.send"), Route("rest/noriRemote.send.view")]
    public async Task<IActionResult> Send()
    {
        if (Caller() is not { } caller)
        {
            return Refused("no caller");
        }
        if (Request.Body.CanSeek)
        {
            Request.Body.Position = 0;
        }
        RemoteOutgoing? outgoing;
        try
        {
            outgoing = await JsonSerializer.DeserializeAsync<RemoteOutgoing>(Request.Body, Json, HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "not a remote frame" });
        }
        if (outgoing == null || _hub.Send(caller, Param("dev"), Param("name"), Param("kind"), outgoing) is not { } seq)
        {
            return Refused("not your room");
        }
        return Answer(new { seq });
    }

    [HttpGet, Route("rest/noriRemote.open"), Route("rest/noriRemote.open.view")]
    public IActionResult Open()
    {
        if (Caller() is not { User: { } user })
        {
            return Refused("only an account opens a jam");
        }
        var auth = AuthParams.Where(k => Param(k).Length > 0).Select(k => new KeyValuePair<string, string>(k, Param(k))).ToList();
        var (room, invite) = _hub.Open(user, Param("dev"), Param("name"), auth);
        return Answer(new { room, invite });
    }

    [HttpGet, Route("rest/noriRemote.close"), Route("rest/noriRemote.close.view")]
    public IActionResult Close() =>
        Caller() is { User: { } user } && _hub.Close(user, Param("room")) ? Answer(new { }) : Refused("not your jam");

    [HttpGet, Route("rest/noriRemote.kick"), Route("rest/noriRemote.kick.view")]
    public IActionResult Kick() =>
        Caller() is { User: { } user } && _hub.Kick(user, Param("room"), Param("member")) ? Answer(new { }) : Refused("not your jam");

    [HttpGet, Route("rest/noriRemote.join"), Route("rest/noriRemote.join.view")]
    public IActionResult Join()
    {
        if (HttpContext.Items[JamGuestMiddleware.CallerItem] is not RemoteKey { Member: null }
            || HttpContext.Items[JamGuestMiddleware.KeyItem] is not string invite
            || _hub.Join(invite, Param("name")) is not { } joined)
        {
            return Refused("join with an invite");
        }
        return Answer(new { room = joined.Room, member = joined.Member, key = joined.Key });
    }

    [HttpGet, Route("rest/noriRemote.leave"), Route("rest/noriRemote.leave.view")]
    public IActionResult Leave() =>
        Caller() is { GuestRoom: { } room, GuestMember: { } member } && _hub.Leave(room, member) ? Answer(new { }) : Refused("not in a jam");
}
