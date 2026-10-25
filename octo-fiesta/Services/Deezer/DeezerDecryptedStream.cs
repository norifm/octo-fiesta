using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace octo_fiesta.Services.Deezer;

public sealed class DeezerDecryptedStream : Stream
{
    private const int ChunkSize = 2048;

    // Deezer's standard Blowfish CBC encryption key for track decryption
    // This is a well-known constant used by the Deezer API, not a user-specific secret
    private const string BfSecret = "g4el58wc0zvf9na1";
    private static readonly byte[] Iv = [0, 1, 2, 3, 4, 5, 6, 7];
    private readonly Stream _source;
    private readonly CbcBlockCipher _cipher;
    private readonly byte[] _currentChunk = new byte[ChunkSize];
    private int _chunkLength = 0;
    private int _chunkOffset = 0;
    private int _chunkIndex = 0;

    public DeezerDecryptedStream(Stream source, string trackId)
    {
        _source = source;
        _cipher = new CbcBlockCipher(new BlowfishEngine());
        _cipher.Init(false, new ParametersWithIV(new KeyParameter(GetBlowfishKey(trackId)), Iv));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        // read and optionally decrypt new chunk from input stream
        if (_chunkOffset >= _chunkLength)
        {
            _chunkLength = await _source.ReadAtLeastAsync(
                _currentChunk,
                ChunkSize,
                throwOnEndOfStream: false,
                cancellationToken);

            if (_chunkLength == 0) return 0;

            // Every 3rd chunk (index % 3 == 0) is encrypted
            if (_chunkIndex % 3 == 0 && _chunkLength == ChunkSize)
            {
                DecryptCurrentChunk();
            }

            _chunkOffset = 0;
            _chunkIndex++;
        }

        // copy (part of) current chunk into destination buffer
        int bytesToCopy = Math.Min(destination.Length, _chunkLength - _chunkOffset);
        _currentChunk.AsSpan(_chunkOffset, bytesToCopy).CopyTo(destination.Span);

        _chunkOffset += bytesToCopy;

        return bytesToCopy;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _source.Dispose();
        base.Dispose(disposing);
    }
    
    public override async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await base.DisposeAsync();
    }

    private static byte[] GetBlowfishKey(string trackId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(trackId));
        var hashHex = Convert.ToHexString(hash).ToLower();
        
        var bfKey = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bfKey[i] = (byte)(hashHex[i] ^ hashHex[i + 16] ^ BfSecret[i]);
        }
        
        return bfKey;
    }

    // Decrypts in place. Each encrypted chunk restarts the CBC chain from the IV.
    private void DecryptCurrentChunk()
    {
        _cipher.Reset();
        var blockSize = _cipher.GetBlockSize(); // 8 bytes for Blowfish

        for (int offset = 0; offset < ChunkSize; offset += blockSize)
        {
            _cipher.ProcessBlock(_currentChunk, offset, _currentChunk, offset);
        }
    }
}