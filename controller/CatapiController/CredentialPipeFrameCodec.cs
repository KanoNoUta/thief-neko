using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CatapiController;

internal enum CredentialPipeFrameStatus
{
    Complete,
    Malformed,
    Oversize,
}

internal readonly record struct CredentialPipeFrameReadResult(
    CredentialPipeFrameStatus Status,
    string? Value = null);

internal sealed class CredentialPipeFrameCodec
{
    internal const int MaxPayloadBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArrayPool<byte> _pool;
    private readonly Func<MemoryStream> _accumulatorFactory;

    internal CredentialPipeFrameCodec(
        ArrayPool<byte>? pool = null,
        Func<MemoryStream>? accumulatorFactory = null)
    {
        _pool = pool ?? ArrayPool<byte>.Shared;
        _accumulatorFactory = accumulatorFactory
            ?? (() => new MemoryStream(MaxPayloadBytes));
    }

    internal async Task<CredentialPipeFrameReadResult> ReadAsync(
        Stream stream,
        CancellationToken ct)
    {
        var rented = _pool.Rent(1024);
        var collected = _accumulatorFactory();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(rented.AsMemory(0, 1024), ct);
                if (count == 0)
                {
                    return new(CredentialPipeFrameStatus.Malformed);
                }

                var newline = rented.AsSpan(0, count).IndexOf((byte)'\n');
                var appendCount = newline >= 0 ? newline : count;
                if (collected.Length + appendCount > MaxPayloadBytes)
                {
                    return new(CredentialPipeFrameStatus.Oversize);
                }

                await collected.WriteAsync(rented.AsMemory(0, appendCount), ct);
                if (newline < 0)
                {
                    continue;
                }

                try
                {
                    return new(
                        CredentialPipeFrameStatus.Complete,
                        StrictUtf8.GetString(
                            collected.GetBuffer(),
                            0,
                            checked((int)collected.Length)));
                }
                catch (DecoderFallbackException)
                {
                    return new(CredentialPipeFrameStatus.Malformed);
                }
            }
        }
        finally
        {
            try
            {
                CryptographicOperations.ZeroMemory(rented.AsSpan());
                _pool.Return(rented);
            }
            finally
            {
                try
                {
                    CryptographicOperations.ZeroMemory(collected.GetBuffer().AsSpan());
                }
                finally
                {
                    collected.Dispose();
                }
            }
        }
    }
}
