using Microsoft.AspNetCore.Mvc;

namespace octo_fiesta.Services.Remote;

/// <summary>
/// The page a nori jam invite opens: <c>&lt;server&gt;/nori/jam#s=&lt;server&gt;&amp;k=&lt;key&gt;</c>. The invite
/// is in the fragment, which browsers never send, so the page reads it in the browser and hands it to the
/// app (on Android through an <c>intent://</c> link, with the releases page as the fallback when nori is not
/// installed). Open to anyone: it holds nothing but the page itself.
/// </summary>
[ApiController]
public sealed class JamInvitePageController : ControllerBase
{
    [HttpGet, Route("nori/jam")]
    public ContentResult Get()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Content(Html, "text/html; charset=utf-8");
    }

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="light dark">
        <title>Join the jam · nori</title>
        <style>
        :root { --bg: #f5f5f7; --card: #fff; --text: #1d1d1f; --dim: #6e6e73; --accent: #fa2d48; --on-accent: #fff; --line: #d2d2d7; }
        @media (prefers-color-scheme: dark) { :root { --bg: #000; --card: #1c1c1e; --text: #f5f5f7; --dim: #98989d; --line: #38383a; } }
        * { box-sizing: border-box; }
        body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 24px 16px;
          background: var(--bg); color: var(--text); font: 16px/1.45 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
        main { width: 100%; max-width: 420px; background: var(--card); border-radius: 20px; padding: 32px 24px; text-align: center; }
        .name { font-weight: 800; font-size: 34px; letter-spacing: -0.03em; color: var(--accent); margin: 0 0 4px; }
        h1 { font-size: 22px; margin: 0 0 8px; }
        p { color: var(--dim); margin: 0 0 24px; }
        .button { display: block; width: 100%; border: 0; border-radius: 14px; padding: 16px; font: inherit; font-weight: 600; font-size: 18px;
          background: var(--accent); color: var(--on-accent); text-decoration: none; cursor: pointer; }
        .button.quiet { background: transparent; color: var(--accent); border: 1px solid var(--line); font-size: 16px; padding: 12px; }
        .link { word-break: break-all; font-size: 13px; color: var(--dim); background: var(--bg); border-radius: 10px; padding: 10px; margin: 0 0 12px; text-align: left; }
        [hidden] { display: none !important; }
        </style>
        </head>
        <body>
        <main>
          <div class="name">nori</div>
          <h1>Join the jam</h1>
          <section id="android" hidden>
            <p>You're invited to a jam. Ask for songs and see what plays.</p>
            <a id="open" class="button" href="#">Open in nori</a>
          </section>
          <section id="other" hidden>
            <p>Open this link on a phone with nori, or paste it into nori on your computer.</p>
            <div id="link" class="link"></div>
            <button id="copy" class="button quiet" type="button">Copy link</button>
          </section>
          <section id="broken" hidden>
            <p>This invite link is incomplete. Ask the host to send it again.</p>
          </section>
        </main>
        <script>
        (function () {
          var releases = "https://github.com/norifm/nori/releases/latest";
          var fragment = new URLSearchParams(location.hash.slice(1));
          var server = fragment.get("s"), key = fragment.get("k");
          function show(id) { document.getElementById(id).hidden = false; }
          if (!server || !key) { show("broken"); return; }
          if (/Android/i.test(navigator.userAgent)) {
            var app = "intent://jam?s=" + encodeURIComponent(server) + "&k=" + encodeURIComponent(key) + "#Intent;scheme=nori;";
            document.getElementById("open").href = app + "package=dev.nori.music;S.browser_fallback_url=" + encodeURIComponent(releases) + ";end";
            show("android");
            // Without a tap Chrome may refuse to open an app and would then follow a fallback; this one has none,
            // so the page stays and the button (with the releases page as fallback) is there.
            location.href = app + "end";
            return;
          }
          var here = location.href;
          document.getElementById("link").textContent = here;
          var copy = document.getElementById("copy");
          copy.addEventListener("click", function () {
            navigator.clipboard.writeText(here).then(function () { copy.textContent = "Copied"; }, function () {
              var range = document.createRange();
              range.selectNodeContents(document.getElementById("link"));
              getSelection().removeAllRanges();
              getSelection().addRange(range);
            });
          });
          show("other");
        })();
        </script>
        </body>
        </html>
        """;
}
