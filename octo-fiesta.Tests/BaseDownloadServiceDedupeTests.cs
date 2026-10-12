using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Common;
using octo_fiesta.Services.Local;
using octo_fiesta.Services;

namespace octo_fiesta.Tests;

public class BaseDownloadServiceDedupeTests : IDisposable
{
    private readonly string _testDownloadPath;

    public BaseDownloadServiceDedupeTests()
    {
        _testDownloadPath = Path.Combine(Path.GetTempPath(), "octo-fiesta-dedupe-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testDownloadPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDownloadPath))
        {
            Directory.Delete(_testDownloadPath, true);
        }
    }

    private FakeDedupeDownloadService BuildService(
        Mock<ILocalLibraryService> localLibMock,
        Mock<IMusicMetadataService> metaMock,
        string? folderTemplate = null,
        bool allowBitrateUpgrade = false)
    {
        var settings = new SubsonicSettings
        {
            FolderTemplate = folderTemplate ?? "{artist}/{album}/{track}. {title}",
            StorageMode = StorageMode.Permanent,
            AllowBitrateUpgrade = allowBitrateUpgrade
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = _testDownloadPath
            })
            .Build();
        return new FakeDedupeDownloadService(
            new Mock<IHttpClientFactory>().Object,
            config,
            localLibMock.Object,
            metaMock.Object,
            settings,
            new Mock<IServiceProvider>().Object,
            NullLogger.Instance);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenFileExistsOnDiskWithoutMapping_RegistersAndSkipsDownload()
    {
        var album = Path.Combine(_testDownloadPath, "Artist", "Album");
        Directory.CreateDirectory(album);
        var expectedFile = Path.Combine(album, "01. Track.flac");
        await File.WriteAllTextAsync(expectedFile, "existing-file");

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "1"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "1"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "1"))
            .ReturnsAsync(new Song
            {
                ExternalId = "1",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 1
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "1");

        Assert.Equal(expectedFile, result);
        Assert.False(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), expectedFile, null, null),
            Times.Once);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenNavidromeReturnsMetadataMatch_RegistersAndSkipsDownload()
    {
        // Navidrome reports a path relative to its library root (== DownloadPath) whose synthesized
        // file name ("01-01 - Track") differs from the real file on disk ("01 - Track").
        var albumDir = Path.Combine(_testDownloadPath, "Artist", "Other Album");
        Directory.CreateDirectory(albumDir);
        var realFile = Path.Combine(albumDir, "01 - Track.flac");
        await File.WriteAllTextAsync(realFile, "existing-file");

        var naviPath = "Artist/Other Album/01-01 - Track.flac"; // does not exist verbatim

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", naviPath));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 8
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        // The real file is located by title and registered with an absolute path + Navidrome ID.
        Assert.Equal(realFile, result);
        Assert.False(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), realFile, null, "local-id-999"),
            Times.Once);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenNavidromeMatchFileMissing_DownloadsInsteadOfRegisteringBrokenPath()
    {
        // Navidrome matches by metadata but the reported path does not resolve to any real local
        // file (different mount, or file deleted). The unverified path must NOT be registered;
        // the service downloads a fresh copy instead.
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", "Artist/Missing Album/01-01 - Track.flac"));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 1
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        var expectedDownloadPath = Path.Combine(_testDownloadPath, "Artist", "Album", "01. Track.flac");
        Assert.Equal(expectedDownloadPath, result);
        Assert.True(service.DownloadTrackCalled);
        localLibMock.Verify(
            x => x.RegisterDownloadedSongAsync(
                It.IsAny<Song>(),
                It.Is<string>(p => p.Contains("Missing Album")),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task DownloadSongAsync_WhenSameAlbumHasDuplicateTitles_DisambiguatesByTrackNumber()
    {
        // Two different tracks in the same album folder share the same title (e.g. a reprise).
        // The reported track number must select the correct file rather than the first match.
        var albumDir = Path.Combine(_testDownloadPath, "Artist", "Other Album");
        Directory.CreateDirectory(albumDir);
        var track3 = Path.Combine(albumDir, "03 - Track.flac");
        var track9 = Path.Combine(albumDir, "09 - Track.flac");
        await File.WriteAllTextAsync(track3, "reprise");
        await File.WriteAllTextAsync(track9, "the-one-we-want");

        // Navidrome reports disc-track "01-09", whose track number (9) should pick track9.
        var naviPath = "Artist/Other Album/01-09 - Track.flac";

        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "2"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "2"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync(new LocalSongMatch("local-id-999", naviPath));

        var metaMock = new Mock<IMusicMetadataService>();
        metaMock
            .Setup(x => x.GetSongAsync("fake", "2"))
            .ReturnsAsync(new Song
            {
                ExternalId = "2",
                ExternalProvider = "fake",
                Title = "Track",
                Artist = "Artist",
                Album = "Album",
                Track = 4
            });

        var service = BuildService(localLibMock, metaMock);

        var result = await service.DownloadSongAsync("fake", "2");

        Assert.Equal(track9, result);
        Assert.False(service.DownloadTrackCalled);
    }

    [Fact]
    public async Task SaveDownloadStream_WhenTargetExists_KeepsExistingFile()
    {
        var localLibMock = new Mock<ILocalLibraryService>();
        localLibMock
            .Setup(x => x.GetMappingForExternalSongAsync("fake", "3"))
            .ReturnsAsync((LocalSongMapping?)null);
        localLibMock
            .Setup(x => x.GetLocalPathForExternalSongAsync("fake", "3"))
            .ReturnsAsync((string?)null);
        localLibMock
            .Setup(x => x.FindLocalSongByMetadataAsync(It.IsAny<Song>()))
            .ReturnsAsync((LocalSongMatch?)null);

        var metaMock = new Mock<IMusicMetadataService>();
        var song = new Song
        {
            ExternalId = "3",
            ExternalProvider = "fake",
            Title = "RaceTrack",
            Artist = "Artist",
            Album = "Album",
            Track = 1
        };
        metaMock.Setup(x => x.GetSongAsync("fake", "3")).ReturnsAsync(song);

        var service = BuildService(localLibMock, metaMock);
        service.CreateFileBeforeWrite = true;

        // AllowBitrateUpgrade is off: the existing file wins and is not overwritten.
        var path = await service.DownloadSongAsync("fake", "3");

        Assert.Equal("race-written", File.ReadAllText(path));
        localLibMock.Verify(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), path, null, null), Times.Once);
    }

    [Theory]
    [InlineData(128, "MP3_128", false)]
    [InlineData(320, "MP3_320", false)]
    [InlineData(128, "MP3_128", true)]
    [InlineData(320, "MP3_320", true)]
    public async Task ExistingUnmappedFile_UpgradeChecksActualAudioAndRecordsStoredQuality(int incomingBitrate, string expectedQuality, bool mapped)
    {
        var directory = Path.Combine(_testDownloadPath, "Artist", "Album");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "01. Track.mp3");
        File.WriteAllBytes(path, DownloadFileStoreTests.Mp3());
        var local = new Mock<ILocalLibraryService>();
        if (mapped) local.Setup(l => l.GetMappingForExternalSongAsync("fake", "1")).ReturnsAsync(new LocalSongMapping
        {
            ExternalId = "1", ExternalProvider = "fake", LocalPath = path, DownloadedQuality = "MP3_128"
        });
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetSongAsync("fake", "1")).ReturnsAsync(new Song
        {
            Title = "Track", Artist = "Artist", Album = "Album", Track = 1,
            ExternalId = "1", ExternalProvider = "fake"
        });
        var service = BuildService(local, metadata, allowBitrateUpgrade: true);
        service.IncomingBitrate = incomingBitrate;
        Assert.Equal(path, await service.DownloadSongAsync("fake", "1"));
        Assert.Equal(expectedQuality, DownloadFileStore.ReadQuality(path));
        local.Verify(x => x.RegisterDownloadedSongAsync(It.IsAny<Song>(), path, expectedQuality, null), Times.Once);
    }

    [Fact]
    public async Task ExistingLossyFile_UpgradeToFlacReplacesAcrossExtensions()
    {
        var directory = Path.Combine(_testDownloadPath, "Artist", "Album");
        Directory.CreateDirectory(directory);
        var oldPath = Path.Combine(directory, "01. Track.mp3");
        File.WriteAllBytes(oldPath, DownloadFileStoreTests.Mp3());
        var local = new Mock<ILocalLibraryService>();
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetSongAsync("fake", "1")).ReturnsAsync(new Song
        {
            Title = "Track", Artist = "Artist", Album = "Album", Track = 1,
            ExternalId = "1", ExternalProvider = "fake"
        });
        var service = BuildService(local, metadata, allowBitrateUpgrade: true);
        service.RealFlac = true;
        var path = await service.DownloadSongAsync("fake", "1");
        Assert.Equal(Path.ChangeExtension(oldPath, ".flac"), path);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(oldPath));
        local.Verify(l => l.RegisterDownloadedSongAsync(It.IsAny<Song>(), path, "FLAC", null), Times.Once);
    }

    private sealed class FakeDedupeDownloadService : BaseDownloadService
    {
        public bool DownloadTrackCalled { get; private set; }
        public bool CreateFileBeforeWrite { get; set; }
        public int? IncomingBitrate { get; set; }
        public bool RealFlac { get; set; }

        protected override string ProviderName => "fake";

        public FakeDedupeDownloadService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILocalLibraryService localLibraryService,
            IMusicMetadataService metadataService,
            SubsonicSettings subsonicSettings,
            IServiceProvider serviceProvider,
            Microsoft.Extensions.Logging.ILogger logger)
            : base(httpClientFactory, configuration, localLibraryService, metadataService, subsonicSettings, serviceProvider, logger)
        {
        }

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override string? ExtractExternalIdFromAlbumId(string albumId) => albumId;

        protected override string? GetTargetQuality() => null;

        protected override Task<DownloadResult> DownloadTrackAsync(string trackId, Song song, CancellationToken cancellationToken)
        {
            DownloadTrackCalled = true;

            if (CreateFileBeforeWrite)
            {
                var racePath = PathHelper.BuildTrackPath(DownloadPath, song, ".flac", SubsonicSettings.FolderTemplate, null);
                Directory.CreateDirectory(Path.GetDirectoryName(racePath)!);
                File.WriteAllText(racePath, "race-written");
            }

            if (RealFlac)
                return Task.FromResult(new DownloadResult(new MemoryStream(DownloadFileStoreTests.Flac()), ".flac", "FLAC"));

            if (IncomingBitrate is { } bitrate)
                return Task.FromResult(new DownloadResult(new MemoryStream(DownloadFileStoreTests.Mp3(bitrate)), ".mp3", $"MP3_{bitrate}"));

            return Task.FromResult(new DownloadResult(
                new MemoryStream(new byte[] { 0x66, 0x4C, 0x61, 0x43 }),
                ".flac",
                "FLAC"));
        }
    }
}
