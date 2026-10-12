using octo_fiesta.Services.Common;

namespace octo_fiesta.Tests;

public class DownloadFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-store-" + Guid.NewGuid());
    public DownloadFileStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    internal static byte[] Mp3(int bitrate = 128)
    {
        var frameLength = 144000 * bitrate / 44100;
        var bytes = new byte[frameLength * 100];
        for (var offset = 0; offset < bytes.Length; offset += frameLength)
        {
            bytes[offset] = 0xff; bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = bitrate == 128 ? (byte)0x90 : (byte)0xe0;
        }
        return bytes;
    }

    // A 0.1-second mono FLAC silence fixture generated with ffmpeg, without padding.
    internal static byte[] Flac() => Convert.FromBase64String("ZkxhQwAAACIEAAQAAAALAAANCsRA8AAAETrqd1mm6BHm3yxYfVK3laE2hAAALAwAAABMYXZmNjMuMS4xMDIBAAAAFAAAAGVuY29kZXI9TGF2ZjYzLjEuMTAy//ipCABQAAAAA5//+KkIAVcAAABv5//4qQgCXgAAANtv//ipCANZAAAAtxf/+HkIBAE5cAAAAPB0");

    private string Existing()
    {
        var path = Path.Combine(_root, "track.mp3");
        File.WriteAllBytes(path, Mp3());
        Assert.Equal("MP3_128", DownloadFileStore.ReadQuality(path));
        return path;
    }

    [Theory]
    [InlineData(false, "MP3_320")]
    [InlineData(true, "MP3_128")]
    [InlineData(true, null)]
    public async Task EqualUnknownOrDisabledUpgrade_KeepsOriginalWithoutReading(bool enabled, string? quality)
    {
        var path = Existing();
        var saved = await DownloadFileStore.SaveAsync(path, path, enabled, quality,
            _ => throw new InvalidOperationException("Must not read incoming audio"), CancellationToken.None);
        Assert.True(saved.ReusedExistingFile);
        Assert.Equal(Mp3(), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task HigherBitrate_ReplacesOnlyAfterStagingCompletes()
    {
        var path = Existing();
        var saved = await DownloadFileStore.SaveAsync(path, path, true, "MP3_320", async stage =>
        {
            Assert.Equal(Mp3(), File.ReadAllBytes(path));
            await File.WriteAllBytesAsync(stage, Mp3(320));
            Assert.Equal(Mp3(), File.ReadAllBytes(path));
            return stage;
        }, CancellationToken.None);
        Assert.False(saved.ReusedExistingFile);
        Assert.Equal("MP3_320", DownloadFileStore.ReadQuality(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task LosslessUpgrade_ChangesExtensionAndRemovesOldCopy()
    {
        var path = Existing();
        var target = Path.ChangeExtension(path, ".flac");
        var saved = await DownloadFileStore.SaveAsync(target, path, true, "FLAC", async stage =>
        {
            await File.WriteAllBytesAsync(stage, Flac());
            return stage;
        }, CancellationToken.None);
        Assert.Equal(target, saved.Path);
        Assert.False(File.Exists(path));
        Assert.Equal(Flac(), File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellation_PreservesOriginalAndCleansStaging(bool cancel)
    {
        var path = Existing();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<Exception>(() => DownloadFileStore.SaveAsync(path, path, true, "MP3_320", async stage =>
        {
            await File.WriteAllTextAsync(stage, "partial");
            if (!cancel) throw new IOException("metadata write failed");
            cts.Cancel();
            return stage;
        }, cts.Token));
        Assert.Equal(Mp3(), File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task DestinationAppearingDuringWrite_IsKeptWhenUpgradesAreDisabled()
    {
        var target = Path.Combine(_root, "track.mp3");
        var saved = await DownloadFileStore.SaveAsync(target, null, false, "MP3_320", async stage =>
        {
            await File.WriteAllBytesAsync(stage, Mp3(320));
            await File.WriteAllBytesAsync(target, Mp3());
            return stage;
        }, CancellationToken.None);
        Assert.True(saved.ReusedExistingFile);
        Assert.Equal(Mp3(), File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncorrectQualityLabelOrBrokenAudio_DoesNotReplaceOriginal(bool broken)
    {
        var path = Existing();
        async Task<DownloadFileStore.SavedFile> Save() => await DownloadFileStore.SaveAsync(path, path, true, "MP3_320", async stage =>
        {
            await File.WriteAllBytesAsync(stage, broken ? [1, 2, 3] : Mp3(128));
            return stage;
        }, CancellationToken.None);
        if (broken) await Assert.ThrowsAnyAsync<Exception>(Save);
        else Assert.True((await Save()).ReusedExistingFile);
        Assert.Equal(Mp3(), File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void LosslessBitDepthUpgrade_RequiresTheExistingAutoUpgradeSetting()
    {
        var path = Path.Combine(_root, "track.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int dataLength = 88200;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataLength);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
        }
        Assert.False(DownloadFileStore.IsUpgrade(path, "FLAC_24"));
        Assert.True(DownloadFileStore.IsUpgrade(path, "FLAC_24", allowLosslessUpgrade: true));
    }

    [Fact]
    public void UnreadableExistingFile_IsNeverReplacedOnAGuess()
    {
        var path = Path.Combine(_root, "broken.mp3");
        File.WriteAllText(path, "broken");
        Assert.False(DownloadFileStore.IsUpgrade(path, "FLAC"));
    }
}
