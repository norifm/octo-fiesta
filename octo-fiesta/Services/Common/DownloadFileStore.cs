namespace octo_fiesta.Services.Common;

/// <summary>Stages audio and metadata before replacing an existing library file.</summary>
public static class DownloadFileStore
{
    public sealed record SavedFile(string Path, bool ReusedExistingFile);

    public static async Task<SavedFile> SaveAsync(
        string outputPath, string? existingPath, bool allowUpgrade, string? incomingQuality,
        Func<string, Task<string>> writeStagedFile, CancellationToken cancellationToken, bool allowLosslessUpgrade = false)
    {
        if (existingPath is not null && File.Exists(existingPath)
            && (!allowUpgrade || !IsUpgrade(existingPath, incomingQuality, allowLosslessUpgrade)))
            return new SavedFile(existingPath, true);

        // Keep the real extension so tag writers can select the correct container.
        var directory = Path.GetDirectoryName(outputPath)!;
        var stage = Path.Combine(directory, $".octo-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
        var written = stage;
        try
        {
            written = await writeStagedFile(stage);
            cancellationToken.ThrowIfCancellationRequested();
            if (existingPath is not null && File.Exists(existingPath)
                && !IsActualUpgrade(existingPath, written, allowLosslessUpgrade))
                return new SavedFile(existingPath, true);
            var target = Path.ChangeExtension(outputPath, Path.GetExtension(written));
            // The format correction may select a different destination that already exists.
            if (File.Exists(target) && !string.Equals(target, existingPath, StringComparison.Ordinal)
                && (!allowUpgrade || !IsActualUpgrade(target, written, allowLosslessUpgrade)))
                return new SavedFile(target, true);
            File.Move(written, target, overwrite: allowUpgrade && File.Exists(target));
            if (existingPath is not null && !string.Equals(existingPath, target, StringComparison.Ordinal))
            {
                // The new file is complete and committed before removing the old container.
                try { File.Delete(existingPath); }
                catch (IOException) { /* A reader may still hold it; keep the valid new file. */ }
                catch (UnauthorizedAccessException) { /* Do not fail after the replacement committed. */ }
            }
            return new SavedFile(target, false);
        }
        finally
        {
            if (File.Exists(written)) File.Delete(written);
            if (written != stage && File.Exists(stage)) File.Delete(stage);
        }
    }

    public static bool IsUpgrade(string existingPath, string? incomingQuality, bool allowLosslessUpgrade = false)
    {
        if (string.IsNullOrWhiteSpace(incomingQuality)) return false;
        try
        {
            using var audio = TagLib.File.Create(existingPath);
            var lossless = IsLossless(audio, existingPath);
            if (lossless)
                return allowLosslessUpgrade && incomingQuality.StartsWith("FLAC_24", StringComparison.OrdinalIgnoreCase)
                    && ((audio.Properties.BitsPerSample > 0 && audio.Properties.BitsPerSample < 24) || (incomingQuality.Equals("FLAC_24_HIGH", StringComparison.OrdinalIgnoreCase)
                        && audio.Properties.AudioSampleRate > 0 && audio.Properties.AudioSampleRate <= 96000));
            if (IsLosslessQuality(incomingQuality)) return true;
            var split = incomingQuality.LastIndexOf('_');
            return split >= 0 && int.TryParse(incomingQuality[(split + 1)..], out var bitrate)
                && audio.Properties.AudioBitrate > 0 && bitrate > audio.Properties.AudioBitrate;
        }
        catch { return false; } // Unknown or damaged files must not be replaced on a guess.
    }

    private static bool IsActualUpgrade(string existingPath, string incomingPath, bool allowLosslessUpgrade)
    {
        using var incoming = TagLib.File.Create(incomingPath);
        if (incoming.Properties.Duration <= TimeSpan.Zero || incoming.Properties.AudioSampleRate <= 0)
            throw new InvalidDataException("Downloaded audio is unreadable; keeping the existing file");
        using var existing = TagLib.File.Create(existingPath);
        var oldLossless = IsLossless(existing, existingPath);
        var newLossless = IsLossless(incoming, incomingPath);
        if (oldLossless)
            return newLossless && allowLosslessUpgrade &&
                (incoming.Properties.BitsPerSample > existing.Properties.BitsPerSample
                    || incoming.Properties.AudioSampleRate > existing.Properties.AudioSampleRate);
        return newLossless || incoming.Properties.AudioBitrate > existing.Properties.AudioBitrate;
    }

    public static string? ReadQuality(string path)
    {
        try
        {
            using var audio = TagLib.File.Create(path);
            if (IsLossless(audio, path))
                return Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)
                    ? (audio.Properties.BitsPerSample > 16 ? (audio.Properties.AudioSampleRate > 96000 ? "FLAC_24_HIGH" : "FLAC_24_LOW") : "FLAC_16") : null;
            var codec = Path.GetExtension(path).ToLowerInvariant() switch { ".mp3" => "MP3", ".m4a" or ".aac" => "AAC", _ => null };
            return codec is not null && audio.Properties.AudioBitrate > 0 ? $"{codec}_{audio.Properties.AudioBitrate}" : null;
        }
        catch { return null; }
    }

    private static bool IsLosslessQuality(string quality) =>
        quality.StartsWith("FLAC", StringComparison.OrdinalIgnoreCase) || quality.StartsWith("ALAC", StringComparison.OrdinalIgnoreCase);

    private static bool IsLossless(TagLib.File audio, string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".flac" or ".wav" or ".aiff" or ".alac"
        || audio.Properties.Codecs.Any(c => c.Description.Contains("ALAC", StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains("FLAC", StringComparison.OrdinalIgnoreCase));
}
