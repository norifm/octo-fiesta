using System.Security.Cryptography;
using System.Text;
using octo_fiesta.Services.Deezer;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace octo_fiesta.Tests;

public class DeezerDecryptedStreamTests
{
    private const string TrackId = "3135556";
    private const int ChunkSize = 2048;

    [Theory]
    [InlineData(0, 4096)]
    [InlineData(1000, 4096)]
    [InlineData(ChunkSize, 1)]
    [InlineData(ChunkSize + 1, 100)]
    [InlineData(ChunkSize * 3, 100)]
    [InlineData(ChunkSize * 7 + 1000, ChunkSize)]
    [InlineData(ChunkSize * 7 + 1000, 81920)]
    [InlineData(5_000_000, 81920)]
    public async Task ReadAsync_ReturnsOriginalAudio(int length, int readSize)
    {
        var plain = RandomAudio(length);
        await using var stream = new DeezerDecryptedStream(new TrickleStream(Encrypt(plain, TrackId)), TrackId);

        var output = new MemoryStream();
        var buffer = new byte[readSize];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public void Read_ReturnsOriginalAudio()
    {
        var plain = RandomAudio(ChunkSize * 10 + 123);
        using var stream = new DeezerDecryptedStream(new TrickleStream(Encrypt(plain, TrackId)), TrackId);

        var output = new MemoryStream();
        stream.CopyTo(output);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task ReadAsync_WithAnotherTrackKey_DoesNotReturnOriginalAudio()
    {
        var plain = RandomAudio(ChunkSize * 3);
        await using var stream = new DeezerDecryptedStream(new MemoryStream(Encrypt(plain, "1")), TrackId);

        var output = new MemoryStream();
        await stream.CopyToAsync(output);

        Assert.NotEqual(plain, output.ToArray());
    }

    internal static byte[] RandomAudio(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Encrypts like the Deezer CDN does. Every third full 2048 byte chunk is Blowfish CBC
    /// encrypted with a key derived from the track id, the rest is left in clear.
    /// </summary>
    internal static byte[] Encrypt(byte[] plain, string trackId)
    {
        var hashHex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(trackId))).ToLower();
        var key = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)(hashHex[i] ^ hashHex[i + 16] ^ "g4el58wc0zvf9na1"[i]);
        }

        var encrypted = (byte[])plain.Clone();
        for (int chunk = 0, offset = 0; offset + ChunkSize <= plain.Length; chunk++, offset += ChunkSize)
        {
            if (chunk % 3 != 0)
            {
                continue;
            }

            var cipher = new CbcBlockCipher(new BlowfishEngine());
            cipher.Init(true, new ParametersWithIV(new KeyParameter(key), [0, 1, 2, 3, 4, 5, 6, 7]));
            for (var block = 0; block < ChunkSize; block += 8)
            {
                cipher.ProcessBlock(plain, offset + block, encrypted, offset + block);
            }
        }

        return encrypted;
    }

    /// <summary>
    /// Hands out data in small pieces of random size, like a network stream.
    /// </summary>
    internal sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        private readonly Random _random = new(data.Length);

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, _random.Next(1, 3000)));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, _random.Next(1, 3000))]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
    }
}
