using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace octo_fiesta.Services.Remote;

/// <summary>
/// The relay's clock, for nori devices to set theirs by as NTP does (nori crates/remote clock.rs): a device
/// sends <c>t1</c> on its clock, the server notes when the request came (<c>t2</c>) and when it answered
/// (<c>t3</c>) on its own. Every member of a jam learns the server's clock this way, so a host's place
/// reaches its guests in one time they share. Answered at once, outside the Subsonic authentication (which
/// would delay one way only), and open to anyone: it tells nothing but the time.
/// </summary>
[ApiController]
public sealed class RemoteTimeController : ControllerBase
{
    /// <summary>The server's clock, µs: monotonic, the same for every request.</summary>
    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    [HttpGet, Route("nori/time")]
    public IActionResult Get([FromQuery] long t1)
    {
        var t2 = NowUs();
        Response.Headers.CacheControl = "no-store";
        return new JsonResult(new { t = "clock", t1, t2, t3 = NowUs() });
    }
}
