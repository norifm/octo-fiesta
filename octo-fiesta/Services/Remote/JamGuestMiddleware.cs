using Microsoft.AspNetCore.Http.Extensions;
using octo_fiesta.Models.Remote;

namespace octo_fiesta.Services.Remote;

/// <summary>
/// Lets a jam guest without an account use the server with the jam host's rights, for what a guest
/// does only: browsing the host's library read only (as nori's Home, Library, Search and its album and
/// artist pages read it), the jam itself, and, while the host lets its guests listen along, streaming the
/// songs of its queue (<see cref="RemoteHub.MayStream"/>). Nothing that writes to the host's account
/// (stars, ratings, scrobbles, playlists, the saved queue) and no playlists at all: the hub cannot tell the
/// host's private ones from its public ones. A guest signs with <c>apiKey=nori-jam-&lt;key&gt;</c>; this
/// swaps that for the host's credentials before the Subsonic authentication runs, and refuses anything
/// else.
/// </summary>
public sealed class JamGuestMiddleware
{
    public const string KeyPrefix = "nori-jam-";

    /// <summary>Where the guest's member is left for <see cref="Controllers.RemoteController"/>.</summary>
    public const string CallerItem = "noriRemoteGuest";

    /// <summary>The jam key itself, for joining with an invite.</summary>
    public const string KeyItem = "noriRemoteKey";

    /// <summary>
    /// What a member may ask: the reads nori's pages make of the library (Home's album shelves and the
    /// albums list, the artists, genres and their songs, an artist's info and top songs, similar and random
    /// songs, lyrics, covers, search), and the jam.
    /// </summary>
    private static readonly HashSet<string> MemberEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping", "search3", "getCoverArt", "getSong", "getAlbum", "getArtist",
        "getAlbumList2", "getArtists", "getArtistInfo2", "getTopSongs", "getGenres", "getSongsByGenre",
        "getRandomSongs", "getSimilarSongs2", "getLyricsBySongId",
        "noriRemote.poll", "noriRemote.send", "noriRemote.leave",
    };

    private readonly RequestDelegate _next;
    private readonly RemoteHub _hub;

    public JamGuestMiddleware(RequestDelegate next, RemoteHub hub)
    {
        _next = next;
        _hub = hub;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var apiKey = context.Request.Query["apiKey"].ToString();
        if (!apiKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "";
        var endpoint = path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) ? path["/rest/".Length..] : "";
        if (endpoint.EndsWith(".view", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint[..^".view".Length];
        }
        var found = _hub.FindKey(apiKey[KeyPrefix.Length..]);
        if (found is not { } f)
        {
            await Refuse(context, 401, 40, "Not a jam key");
            return;
        }
        var allowed = f.Key.Member == null
            ? endpoint.Equals("noriRemote.join", StringComparison.OrdinalIgnoreCase)
            : MemberEndpoints.Contains(endpoint)
                || (endpoint.Equals("stream", StringComparison.OrdinalIgnoreCase) && _hub.MayStream(f.Key.Room, context.Request.Query["id"].ToString()));
        if (!allowed)
        {
            await Refuse(context, 403, 50, "Not for a jam guest");
            return;
        }
        var query = new QueryBuilder(context.Request.Query
            .Where(q => q.Key is not ("apiKey" or "u" or "p" or "t" or "s"))
            .SelectMany(q => q.Value.Select(v => new KeyValuePair<string, string>(q.Key, v ?? ""))));
        foreach (var kv in f.HostAuth)
        {
            query.Add(kv.Key, kv.Value);
        }
        context.Request.QueryString = query.ToQueryString();
        context.Items[CallerItem] = f.Key;
        context.Items[KeyItem] = apiKey[KeyPrefix.Length..];
        await _next(context);
    }

    private static async Task Refuse(HttpContext context, int status, int code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new Dictionary<string, object>
        {
            ["subsonic-response"] = new { status = "failed", version = "1.16.1", error = new { code, message } },
        };
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(body));
    }
}
