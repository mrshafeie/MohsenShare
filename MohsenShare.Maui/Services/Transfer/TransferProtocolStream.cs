
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MohsenShare.Maui.Services.Transfer;

public sealed class TransferProtocolStream
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TransferProtocolStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException(
                "The stream must support reading and writing.",
                nameof(stream));
        }
    }

    /// <summary>
    /// Writes one JSON control message followed by a newline.
    /// </summary>
    public async Task WriteMessageAsync(
        TransferMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] data = TransferProtocol.SerializeMessage(message);

        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _stream.WriteAsync(
                data.AsMemory(),
                cancellationToken);

            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads one newline-terminated JSON control message.
    /// Returns null only when the connection closes before
    /// any byte of a new message is received.
    /// </summary>
    public async Task<TransferMessage?> ReadMessageAsync(
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();

        byte[] singleByte = new byte[1];

        while (true)
        {
            int read = await _stream.ReadAsync(
                singleByte.AsMemory(0, 1),
                cancellationToken);

            if (read == 0)
            {
                if (buffer.Length == 0)
                {
                    return null;
                }

                throw new EndOfStreamException(
                    "Connection closed in the middle of a control message.");
            }

            byte value = singleByte[0];

            if (value == (byte)'\n')
            {
                break;
            }

            if (buffer.Length >= TransferProtocol.MaxControlMessageBytes)
            {
                throw new InvalidDataException(
                    "Control message exceeds the permitted size.");
            }

            buffer.WriteByte(value);
        }

        byte[] messageBytes = buffer.ToArray();

        int length = messageBytes.Length;

        // Allow CRLF as well as LF line endings.
        if (length > 0 && messageBytes[length - 1] == (byte)'\r')
        {
            Array.Resize(ref messageBytes, length - 1);
        }

        if (messageBytes.Length == 0)
        {
            throw new InvalidDataException(
                "An empty control message is not valid.");
        }

        return TransferProtocol.DeserializeMessage(messageBytes);
    }

    /// <summary>
    /// Writes exactly the requested number of bytes.
    /// Used for binary file chunks after a control message.
    /// </summary>
    public async Task WriteExactlyAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _stream.WriteAsync(data, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads exactly the requested number of bytes.
    /// Throws if the connection closes before all bytes arrive.
    /// </summary>
    public async Task ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        int totalRead = 0;

        while (totalRead < destination.Length)
        {
            int read = await _stream.ReadAsync(
                destination[totalRead..],
                cancellationToken);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Connection closed before the complete binary block arrived.");
            }

            totalRead += read;
        }
    }

    public async Task CopyExactlyAsync(
        Stream source,
        long length,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        byte[] buffer = new byte[64 * 1024];
        long totalCopied = 0;

        while (totalCopied < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(
                buffer.Length,
                length - totalCopied);

            int read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The source stream ended before the expected length.");
            }

            await WriteExactlyAsync(
                buffer.AsMemory(0, read),
                cancellationToken);

            totalCopied += read;
            progress?.Report(totalCopied);
        }
    }

    public async Task CopyExactlyFromConnectionAsync(
        Stream destination,
        long length,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        byte[] buffer = new byte[64 * 1024];
        long totalCopied = 0;

        while (totalCopied < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(
                buffer.Length,
                length - totalCopied);

            await ReadExactlyAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);

            await destination.WriteAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);

            totalCopied += requested;
            progress?.Report(totalCopied);
        }

        await destination.FlushAsync(cancellationToken);
    }
}
