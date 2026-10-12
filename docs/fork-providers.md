# Fork provider features

The provider lists, GDStudio integration, provider-specific folders, and bitrate replacement originated in [Derek Zhu’s](https://github.com/hereisderek) contributions in PRs [#22](https://github.com/norifm/octo-fiesta/pull/22), [#23](https://github.com/norifm/octo-fiesta/pull/23), [#24](https://github.com/norifm/octo-fiesta/pull/24), and [#25](https://github.com/norifm/octo-fiesta/pull/25). The consolidated implementation preserves his original commits.

These features are optional. Existing single-provider `MUSIC_SERVICE` values, the default download folder, automatic Apple Music integration when alacarte is configured, playlist support, and `AUTO_UPGRADE_QUALITY` remain available. Settings below are added by the fork; upstream startup and controller behavior are preserved for existing configurations.

## Multiple providers

Set `MUSIC_SERVICE=Deezer,Qobuz,GDStudio` in Compose's `.env`, or `Subsonic__MusicService=Deezer,Qobuz,GDStudio` when running directly. Commas, semicolons, and pipes are accepted; names are case insensitive and duplicates are removed. `Subsonic__MusicServices` is also accepted as an explicit list setting. Unknown names produce a clear startup error.

Searches run in parallel and results are interleaved within the requested limit. IDs retain the provider, so lookups, covers, streams, downloads, and playlists route to the correct service. Failed or timed-out providers do not discard successful results from others. Provider calls have a 20-second composite timeout; the metadata interface cannot cancel work that has already started.

When several providers are requested, providers without configured credentials are skipped with a startup warning. A list with no usable provider fails to start. A single provider retains its existing startup and validation behavior. Configured Apple Music is still added automatically; adding `AppleMusic` explicitly is optional. Existing Deezer/Qobuz secondary playlist registrations stay intact for single-provider configurations.

## Per-provider folders

A provider-specific path overrides `Library__DownloadPath` only for that provider. Empty overrides use the shared folder. For direct execution:

```env
AppleMusic__DownloadPath=/music/apple
Qobuz__DownloadPath=/music/qobuz
```

The supplied Compose file maps `APPLE_MUSIC_DOWNLOAD_PATH`, `QOBUZ_DOWNLOAD_PATH`, `DEEZER_DOWNLOAD_PATH`, `TIDAL_DOWNLOAD_PATH`, `YANDEX_DOWNLOAD_PATH`, `SQUIDWTF_DOWNLOAD_PATH`, and `GDSTUDIO_DOWNLOAD_PATH` from `.env` into the corresponding container settings. An arbitrary `AppleMusic__DownloadPath` entry in Compose's `.env` is not automatically passed into a container.

For example, set `APPLE_MUSIC_DOWNLOAD_PATH=/music/apple` and add a mount to the service:

```yaml
services:
  octo-fiesta:
    volumes:
      - /srv/alacarte-music:/music/apple
```

alacarte and octo-fiesta must agree on the Apple Music path and see the same files. Navidrome must also index any provider-specific folders. These overrides do not create mounts or change the Navidrome upload API's library-root mapping.

## Bitrate replacement

`ALLOW_BITRATE_UPGRADE=false` is the default (`Subsonic__AllowBitrateUpgrade` directly). With it enabled, existing downloaded/imported files can be compared against an incoming download. A higher lossy bitrate can replace a lower one, and lossless can replace lossy. Existing lossless or unreadable audio is kept. This option does not compare lossless bit depths or sample rates; `AUTO_UPGRADE_QUALITY` remains the existing provider-quality upgrade setting.

Audio and metadata are staged under a unique temporary name before replacement. Cancellation or failure during staging preserves the original file. A container change, such as MP3 to FLAC, writes the new file before removing the old copy. A skipped download records the existing file's quality rather than the rejected download's quality. Downloads written by alacarte and downloads ingested through the Navidrome upload API continue to follow their own storage paths.

## Optional origin labels

`SHOW_EXTERNAL_PROVIDER_IN_SUFFIX=false` is the default (`Subsonic__ShowExternalProviderInSuffix` directly), preserving the `Remote` suffix for undownloaded external songs. Enable it to display labels such as `AppleMusic` or `GDStudio-Apple`. Downloaded files retain their actual format; transcode decisions still use a real container name.

## GDStudio

Add `GDStudio` to `MUSIC_SERVICE` to enable the provider. No account credentials are required. Tracks, albums, and artists are supported; playlists are not.

| Compose variable | Direct setting | Default |
|---|---|---|
| `GDSTUDIO_SOURCE` | `GDStudio__Source` | `netease` |
| `GDSTUDIO_TIMEOUT_SECONDS` | `GDStudio__TimeoutSeconds` | `8` |
| `GDSTUDIO_BR` | `GDStudio__Br` | `999` |
| `GDSTUDIO_API` | `GDStudio__Api` | `https://music-api.gdstudio.xyz/api.php` |
| `GDSTUDIO_PROXY` | `GDStudio__Proxy` | empty |

`Source` accepts a comma-separated list such as `netease,joox` or `apple`. Each source is queried independently; a failed source is skipped. Search/metadata calls use the configured timeout, with three times that allowance for a source's first call. Supported bitrates are `128`, `192`, `320`, `740` (16-bit lossless), and `999` (24-bit lossless). When a requested quality returns no URL, the next lower value is tried once. Proxies accept HTTP, HTTPS, or SOCKS5 URLs.

Identical queries share in-flight work and are cached for an hour in a bounded cache. At most four search requests run at once. A 429/503 response is retried once. Artist browsing checks up to three pages per source; opening an album searches up to ten pages and uses the first source with matching tracks, avoiding duplicate copies from different sources. Albums and artists are derived from upstream search results, so coverage and ordering depend on that upstream catalogue. Search previews are never treated as complete album listings.

GDStudio has no track metadata lookup by ID. Unknown IDs require a fresh search; the provider will not create a download with invented artist/title tags. Search metadata can expire or be evicted. Already downloaded tracks keep using local mappings.

For known catalogue romanizations, explicit artist aliases can be supplied in `appsettings.json`:

```json
{
  "GDStudio": {
    "ArtistAliases": { "周杰伦": ["Jay Chou"] }
  }
}
```

Aliases let artist browsing match a known alternative name without altering upstream artist tags. Arbitrary song-title or album-title searches never become artist names.
