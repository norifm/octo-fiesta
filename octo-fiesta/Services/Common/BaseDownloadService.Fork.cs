using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Download;
using octo_fiesta.Services.Subsonic;
using IOFile = System.IO.File;
using IODirectory = System.IO.Directory;
using System.Text.RegularExpressions;

namespace octo_fiesta.Services.Common;

// Fork additions to BaseDownloadService (unmapped-file reuse, bitrate upgrades, Navidrome upload API),
// kept out of the upstream file so syncing with upstream touches only one-line hooks there.
public abstract partial class BaseDownloadService
{
    /// <summary>
    /// Lazy-loaded Navidrome upload service. Returns null when not registered.
    /// </summary>
    private INavidromeUploadService? _navidromeUploadService;
    protected INavidromeUploadService? NavidromeUploadService
    {
        get
        {
            if (_navidromeUploadService == null)
            {
                _navidromeUploadService = _serviceProvider.GetService<INavidromeUploadService>();
            }
            return _navidromeUploadService;
        }
    }

    /// <summary>
    /// No mapping — the file may have been imported by another tool (e.g. alacarte). Probes the expected
    /// path (then Navidrome) before hitting the network. Registers and completes <paramref name="info"/>
    /// when a file is found, or returns it as the bitrate-upgrade candidate when upgrades are allowed.
    /// </summary>
    private async Task<string?> ForkProbeUnmappedAsync(string externalProvider, string externalId, DownloadInfo info)
    {
        var probeSong = await GetSongMetadataForTrackAsync(externalProvider, externalId);
        var existingPath = ProbeForExistingFile(
            PathHelper.BuildTrackPath(DownloadPath, probeSong, ".flac", SubsonicSettings.FolderTemplate, null));
        string? existingSubsonicId = null;
        if (existingPath == null)
        {
            // Fallback: ask Navidrome — catches files with metadata-only matches.
            // Navidrome reports paths relative to its library root and synthesizes the
            // file name from tags (e.g. "01-01 - Title.mp3"), so it can't be trusted
            // verbatim. Resolve to a real local file before registering — otherwise the
            // mapping's File.Exists check always fails, so it never resolves and gets
            // re-probed (and re-logged) on every play, and streaming fails.
            var naviMatch = await LocalLibraryService.FindLocalSongByMetadataAsync(probeSong);
            if (naviMatch != null)
            {
                var resolvedPath = ResolveNavidromeMatchFile(naviMatch.LocalPath, probeSong);
                if (resolvedPath != null)
                {
                    existingPath = resolvedPath;
                    existingSubsonicId = naviMatch.LocalSubsonicId;
                }
            }
        }
        if (existingPath == null || SubsonicSettings.AllowBitrateUpgrade)
        {
            return existingPath;
        }
        Logger.LogInformation("Found existing local file without mapping, registering: {Path}", existingPath);
        await LocalLibraryService.RegisterDownloadedSongAsync(probeSong, existingPath, downloadedQuality: null, localSubsonicId: existingSubsonicId);
        info.Status = DownloadStatus.Completed;
        info.CompletedAt = DateTime.UtcNow;
        info.LocalPath = existingPath;
        return null;
    }

    /// <summary>
    /// With bitrate upgrades allowed, an existing file is not reused or backed up as is: it becomes the
    /// candidate the fresh download is compared against. Returns true when it was taken as that candidate.
    /// </summary>
    private bool ForkTakeBitrateCandidate(string existingPath, ref string? bitrateCandidate)
    {
        if (!SubsonicSettings.AllowBitrateUpgrade) return false;
        bitrateCandidate ??= existingPath;
        return true;
    }

    /// <summary>
    /// Stores a provider download: in place when the provider already put it in the library, through the
    /// Navidrome upload API when configured, else on disk (keeping the better of new and existing file).
    /// Returns the local path and, for uploads, the Navidrome song id.
    /// </summary>
    private async Task<(string LocalPath, string? NavidromeUploadId)> ForkSaveDownloadAsync(DownloadResult downloadResult,
        Song song, bool isCache, DownloadInfo info, string? bitrateCandidate, CancellationToken cancellationToken)
    {
        if (downloadResult.LibraryPath != null)
        {
            return (downloadResult.LibraryPath, null);
        }

        // Use the Navidrome upload API when enabled and we are not in cache mode and
        // not currently performing a same-path quality upgrade (which relies on backup/replace).
        var uploadService = NavidromeUploadService;
        var useUploadApi = !isCache
            && string.IsNullOrEmpty(info.BackupPath)
            && bitrateCandidate == null
            && uploadService != null
            && uploadService.IsConfigured;

        await using (downloadResult.DownloadStream)
        {
            if (!useUploadApi)
            {
                return (await ForkSaveDownloadStreamToFileAsync(downloadResult, song, isCache, cancellationToken, bitrateCandidate), null);
            }

            var uploaded = await UploadDownloadStreamToNavidromeAsync(
                downloadResult, song, uploadService!, cancellationToken);

            if (uploaded == null)
            {
                throw new InvalidOperationException(
                    "Navidrome upload failed; aborting download to avoid silent fallback");
            }

            return (uploaded.Value.LocalPath, uploaded.Value.NavidromeId);
        }
    }

    /// <summary>
    /// Album downloads: registers a track found on disk without a mapping instead of re-downloading it.
    /// </summary>
    private async Task<bool> ForkRegisterProbedTrackAsync(Song track)
    {
        var probedPath = ProbeForExistingFile(
            PathHelper.BuildTrackPath(DownloadPath, track, ".flac", SubsonicSettings.FolderTemplate, null));
        if (probedPath == null)
        {
            return false;
        }
        Logger.LogDebug("Track {TrackId} found on disk without mapping, skipping re-download", track.ExternalId);
        await LocalLibraryService.RegisterDownloadedSongAsync(track, probedPath, downloadedQuality: null);
        return true;
    }

    /// <summary>
    /// Takes DownloadResult provided by specific provider and saves it to file, keeping the existing file
    /// instead when it is as good (see <see cref="DownloadFileStore"/>).
    /// </summary>
    private async Task<string> ForkSaveDownloadStreamToFileAsync(DownloadResult result, Song song, bool toCache,
        CancellationToken cancellationToken, string? bitrateCandidate = null)
    {
        var basePath = toCache ? CachePath : DownloadPath;
        var outputPath = PathHelper.BuildTrackPath(basePath, song, result.Extension, SubsonicSettings.FolderTemplate, result.DownloadedQuality);
        EnsureDirectoryExists(Path.GetDirectoryName(outputPath)!);
        var existingPath = bitrateCandidate ?? ProbeForExistingFile(outputPath);
        var saved = await DownloadFileStore.SaveAsync(outputPath, existingPath,
            SubsonicSettings.AllowBitrateUpgrade, result.DownloadedQuality, async stage =>
            {
                var written = stage;
                try
                {
                    await using (var outputFile = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await CopyWithProgressAsync(result.DownloadStream, outputFile, song.Title, cancellationToken);
                    written = CorrectExtensionIfNeeded(stage);
                    await WriteMetadataAsync(written, song, cancellationToken);
                    PatchMp4DurationIfNeeded(written, result);
                    return written;
                }
                catch
                {
                    TryDeleteIncompleteFile(written);
                    throw;
                }
            }, cancellationToken, SubsonicSettings.AutoUpgradeQuality);
        if (saved.ReusedExistingFile)
        {
            result.StoredQuality = DownloadFileStore.ReadQuality(saved.Path);
            Logger.LogInformation("Keeping existing file: {Path}", saved.Path);
        }
        else
        {
            Logger.LogInformation("Downloaded file to: {Path}", saved.Path);
            if (!toCache && LyricsService is { Enabled: true } sidecarService)
                _ = Task.Run(() => sidecarService.TryWriteSidecarAsync(saved.Path, song, CancellationToken.None));
        }
        return saved.Path;
    }

    /// <summary>
    /// Saves the freshly downloaded stream to a temp file (with metadata) and uploads it to
    /// Navidrome via the custom <c>POST /api/upload</c> endpoint. The temp file is deleted on
    /// completion (success or failure) - Navidrome stores its own copy in the library.
    /// </summary>
    /// <returns>
    /// A tuple of (LocalPath, NavidromeId) when the upload succeeds. LocalPath is computed by
    /// joining <see cref="DownloadPath"/> with the relative path returned by Navidrome (assumes
    /// the configured DownloadPath maps to the Navidrome library root - typical docker-compose
    /// setup). Returns <c>null</c> when the upload fails so the caller can decide what to do.
    /// </returns>
    protected async Task<(string LocalPath, string NavidromeId)?> UploadDownloadStreamToNavidromeAsync(
        DownloadResult result,
        Song song,
        INavidromeUploadService uploadService,
        CancellationToken cancellationToken)
    {
        // Stage the file in a scratch dir outside the library so Navidrome only ever sees the
        // copy it ingests via the upload API.
        var stagingRoot = Path.Combine(Path.GetTempPath(), "octo-fiesta-upload");
        EnsureDirectoryExists(stagingRoot);

        var stagingPath = PathHelper.BuildTrackPath(
            stagingRoot, song, result.Extension, SubsonicSettings.FolderTemplate, result.DownloadedQuality);
        EnsureDirectoryExists(Path.GetDirectoryName(stagingPath)!);
        stagingPath = PathHelper.ResolveUniquePath(stagingPath);

        try
        {
            await using (var outputFile = IOFile.Create(stagingPath))
            {
                await result.DownloadStream.CopyToAsync(outputFile, cancellationToken);
            }

            await WriteMetadataAsync(stagingPath, song, cancellationToken);

            // Compute the destination folder for the upload using the same artist/album layout
            // as the local download path, optionally prefixed by NavidromeUploadFolder.
            var fileName = Path.GetFileName(stagingPath);
            var stagingDir = Path.GetDirectoryName(stagingPath)!;
            var relativeDir = Path.GetRelativePath(stagingRoot, stagingDir).Replace('\\', '/');

            var prefix = SubsonicSettings.NavidromeUploadFolder?.Trim().Trim('/');
            string folder;
            if (string.IsNullOrEmpty(prefix))
            {
                folder = relativeDir == "." ? string.Empty : relativeDir;
            }
            else
            {
                folder = relativeDir == "." ? prefix : $"{prefix}/{relativeDir}";
            }

            var uploadResult = await uploadService.UploadFileAsync(stagingPath, folder, fileName, cancellationToken);
            if (uploadResult == null)
            {
                return null;
            }

            // Best-effort local path: assumes DownloadPath is mounted at the Navidrome library root.
            // If it isn't, the file simply won't exist locally - the Navidrome ID is what matters.
            var localPath = Path.Combine(
                DownloadPath,
                uploadResult.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!IOFile.Exists(localPath))
            {
                Logger.LogDebug(
                    "Uploaded track is not visible at expected local path {LocalPath} (DownloadPath may not be mounted as the Navidrome library root)",
                    localPath);
            }

            return (localPath, uploadResult.Id);
        }
        finally
        {
            try
            {
                if (IOFile.Exists(stagingPath))
                {
                    IOFile.Delete(stagingPath);
                }

                // Clean up empty parent directories under the staging root to avoid clutter.
                var dir = Path.GetDirectoryName(stagingPath);
                while (!string.IsNullOrEmpty(dir)
                    && dir.StartsWith(stagingRoot, StringComparison.Ordinal)
                    && dir.Length > stagingRoot.Length
                    && IODirectory.Exists(dir)
                    && !IODirectory.EnumerateFileSystemEntries(dir).Any())
                {
                    IODirectory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to clean up staging file {Path}", stagingPath);
            }
        }
    }

    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a"];

    // Matches a leading track / disc-track numbering prefix such as "01 - ", "01-01 - ",
    // "1.01 - " or "01_01 - " so the remaining title can be compared against the song title.
    private static readonly Regex TrackNumberPrefixRegex = new(
        @"^\s*\d{1,3}([\-_.]\d{1,3})?\s*[-_.]\s*",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Locates the real local file for a Navidrome metadata match. Navidrome returns paths relative
    /// to its library root (assumed mounted at <see cref="DownloadPath"/>) and synthesizes the file
    /// name from tags, so the disc/track prefix often differs from the actual file on disk
    /// (e.g. reported "01-01 - Title.mp3" vs real "01 - Title.mp3"). The directory portion is
    /// reliable, so when the exact file is missing we scan it for an audio file whose title matches.
    /// Returns the real path, or null when none exists (caller then downloads a fresh copy).
    /// </summary>
    private string? ResolveNavidromeMatchFile(string? navidromePath, Song song)
    {
        if (string.IsNullOrWhiteSpace(navidromePath))
        {
            return null;
        }

        var resolved = Path.IsPathRooted(navidromePath)
            ? navidromePath
            : Path.Combine(DownloadPath, navidromePath.Replace('/', Path.DirectorySeparatorChar));

        if (IOFile.Exists(resolved))
        {
            return resolved;
        }

        var dir = Path.GetDirectoryName(resolved);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var titleKey = StringNormalizer.CreateSongTitleDedupeKey(song.Title);
        if (string.IsNullOrEmpty(titleKey))
        {
            return null;
        }

        // Audio files in this album folder whose title matches (after stripping the track/disc prefix).
        var matches = Directory.EnumerateFiles(dir)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => StringNormalizer.CreateSongTitleDedupeKey(
                TrackNumberPrefixRegex.Replace(Path.GetFileNameWithoutExtension(f), string.Empty)) == titleKey)
            .ToList();

        if (matches.Count == 1)
        {
            Logger.LogDebug("Navidrome reported '{Reported}' but matched real file '{Actual}' by title",
                navidromePath, matches[0]);
            return matches[0];
        }

        if (matches.Count > 1)
        {
            // Multiple tracks in the same album folder share this title (e.g. a reprise or a deluxe
            // edition duplicate). Disambiguate by the track number Navidrome encoded in the reported
            // file name; if that doesn't pin a single file, skip reuse and download to avoid serving
            // the wrong track.
            var reportedTrack = ExtractTrackNumber(Path.GetFileNameWithoutExtension(resolved));
            if (reportedTrack != null)
            {
                var byTrack = matches
                    .Where(f => ExtractTrackNumber(Path.GetFileNameWithoutExtension(f)) == reportedTrack)
                    .ToList();
                if (byTrack.Count == 1)
                {
                    return byTrack[0];
                }
            }

            Logger.LogWarning(
                "Multiple local files in '{Dir}' match title '{Title}'; skipping reuse to avoid a wrong match",
                dir, song.Title);
        }

        return null;
    }

    /// <summary>
    /// Extracts the track number from a leading "NN - " or "DD-NN - " file-name prefix.
    /// Returns the last number in the prefix (the track within its disc), or null when absent.
    /// </summary>
    private static int? ExtractTrackNumber(string stem)
    {
        var prefix = TrackNumberPrefixRegex.Match(stem);
        if (!prefix.Success)
        {
            return null;
        }

        var numbers = Regex.Matches(prefix.Value, @"\d{1,3}");
        if (numbers.Count == 0)
        {
            return null;
        }

        return int.Parse(numbers[^1].Value);
    }

    private static string? ProbeForExistingFile(string basePath)
    {
        var dir = Path.GetDirectoryName(basePath);
        var stemWithoutExt = Path.GetFileNameWithoutExtension(basePath);
        if (dir == null || !Directory.Exists(dir))
        {
            return null;
        }
        foreach (var ext in AudioExtensions)
        {
            var candidate = Path.Combine(dir, stemWithoutExt + ext);
            if (IOFile.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
