
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using MohsenShare.Maui.Services.Storage;

namespace MohsenShare.Maui.Services.Transfer;

public sealed class TransferSessionService : ITransferSessionService
{
    private readonly TransferResumeStore _resumeStore;
    private readonly TransferPartialFileStore _partialStore;
    private readonly ReceivedFileStorageService _storage;
    private readonly TransferQueue _queue = new();

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateLock = new();

    private TcpListener? _listener;
    private TcpClient? _client;
    private TransferProtocolStream? _protocol;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;

    private TransferConnectionInfo? _currentConnection;
    private TransferSessionState _state = TransferSessionState.Disconnected;
    private bool _isReceiver;
    private bool _disposed;

    public TransferSessionService(
        TransferResumeStore resumeStore,
        TransferPartialFileStore partialStore,
        ReceivedFileStorageService storage)
    {
        _resumeStore = resumeStore;
        _partialStore = partialStore;
        _storage = storage;
    }

    public event EventHandler<TransferSessionStateChangedEventArgs>?
        SessionStateChanged;

    public event EventHandler<TransferProgressChangedEventArgs>?
        ProgressChanged;

    public event EventHandler<TransferFileReceivedEventArgs>?
        FileReceived;

    public event EventHandler<TransferErrorEventArgs>?
        ErrorOccurred;

    public TransferSessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public TransferConnectionInfo? CurrentConnection =>
        _currentConnection;

    public async Task<TransferConnectionInfo> StartListeningAsync(
        string? localDeviceName = null,
        IPAddress? preferredAddress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_listener is not null)
        {
            throw new InvalidOperationException(
                "The receiver is already listening.");
        }

        IPAddress advertisedAddress =
            preferredAddress ?? GetLocalIPv4Address()
            ?? throw new InvalidOperationException(
                "No local IPv4 address was found. Connect both devices " +
                "to the same Wi-Fi network or hotspot.");

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();

        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        string sessionId = Guid.NewGuid().ToString("N");
        string token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        _isReceiver = true;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        _currentConnection = new TransferConnectionInfo
        {
            SessionId = sessionId,
            DeviceName = GetDeviceName(localDeviceName),
            IpAddress = advertisedAddress.ToString(),
            Port = port,
            Token = token
        };

        SetState(TransferSessionState.Connecting,
            "Waiting for a device to connect.");

        _acceptTask = AcceptLoopAsync(_lifetime.Token);

        return _currentConnection;
    }

    public async Task ConnectAsync(
        string ipAddress,
        int port,
        string token,
        string? localDeviceName = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (State == TransferSessionState.Connected)
        {
            throw new InvalidOperationException(
                "A transfer session is already connected.");
        }

        if (!IPAddress.TryParse(ipAddress, out IPAddress? address))
        {
            throw new ArgumentException(
                "The IP address is invalid.",
                nameof(ipAddress));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException(
                "The connection token is required.",
                nameof(token));
        }

        SetState(TransferSessionState.Connecting,
            "Connecting to the receiving device.");

        var client = new TcpClient(address.AddressFamily);

        try
        {
            await client.ConnectAsync(address, port, cancellationToken);

            var protocol = new TransferProtocolStream(
                client.GetStream());

            await protocol.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.Hello,
                    Token = token,
                    DeviceName = GetDeviceName(localDeviceName)
                },
                cancellationToken);

            TransferMessage? response =
                await protocol.ReadMessageAsync(cancellationToken);

            if (response is null ||
                response.Type != TransferMessageTypes.HelloAccepted ||
                string.IsNullOrWhiteSpace(response.SessionId))
            {
                throw new InvalidDataException(
                    "The receiver did not accept the connection.");
            }

            _client = client;
            _protocol = protocol;
            _isReceiver = false;

            _currentConnection = new TransferConnectionInfo
            {
                SessionId = response.SessionId,
                DeviceName = response.DeviceName ?? ipAddress,
                IpAddress = ipAddress,
                Port = port,
                Token = token
            };

            SetState(TransferSessionState.Connected,
                "Connected to the receiving device.");
        }
        catch
        {
            client.Dispose();
            SetState(TransferSessionState.Failed,
                "Could not establish the connection.");
            throw;
        }
    }

    private async Task AcceptLoopAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener!
                    .AcceptTcpClientAsync(cancellationToken);

                // One active connection at a time.
                if (_client is not null)
                {
                    client.Dispose();
                    continue;
                }

                _client = client;

                try
                {
                    _protocol = new TransferProtocolStream(
                        client.GetStream());

                    await ProcessReceiverConnectionAsync(
                        _protocol,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(
                        this,
                        new TransferErrorEventArgs(
                            "The receiver connection ended unexpectedly.",
                            exception: ex));
                }
                finally
                {
                    _protocol = null;
                    _client?.Dispose();
                    _client = null;

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SetState(TransferSessionState.Connecting,
                            "Waiting for a device to connect.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // The listener was stopped.
        }
        catch (Exception ex)
        {
            SetState(TransferSessionState.Failed, ex.Message);

            ErrorOccurred?.Invoke(
                this,
                new TransferErrorEventArgs(
                    "The receiver could not continue listening.",
                    exception: ex));
        }
    }

    private async Task ProcessReceiverConnectionAsync(
        TransferProtocolStream protocol,
        CancellationToken cancellationToken)
    {
        TransferMessage? hello =
            await protocol.ReadMessageAsync(cancellationToken);

        if (hello is null ||
            hello.Type != TransferMessageTypes.Hello ||
            !string.Equals(
                hello.Token,
                _currentConnection?.Token,
                StringComparison.Ordinal))
        {
            await protocol.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.Error,
                    ErrorCode = "invalidHandshake",
                    Message = "The connection token is invalid."
                },
                cancellationToken);

            return;
        }

        await protocol.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.HelloAccepted,
                SessionId = _currentConnection!.SessionId,
                DeviceName = _currentConnection.DeviceName
            },
            cancellationToken);

        SetState(TransferSessionState.Connected,
            "A device connected.");

        while (!cancellationToken.IsCancellationRequested)
        {
            TransferMessage? message =
                await protocol.ReadMessageAsync(cancellationToken);

            if (message is null)
            {
                break;
            }

            if (message.Type == TransferMessageTypes.EndSession)
            {
                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.SessionEnded,
                        SessionId = _currentConnection.SessionId
                    },
                    cancellationToken);

                break;
            }

            switch (message.Type)
            {
                case TransferMessageTypes.FileStart:
                    await ReceiveFileAsync(
                        protocol,
                        message,
                        cancellationToken);
                    break;

                case TransferMessageTypes.Ping:
                    await protocol.WriteMessageAsync(
                        new TransferMessage
                        {
                            Type = TransferMessageTypes.Pong,
                            SessionId = _currentConnection.SessionId
                        },
                        cancellationToken);
                    break;

                default:
                    await protocol.WriteMessageAsync(
                        new TransferMessage
                        {
                            Type = TransferMessageTypes.Error,
                            ErrorCode = "unsupportedMessage",
                            Message = $"Unsupported message type: {message.Type}"
                        },
                        cancellationToken);
                    break;
            }
        }
    }

    private async Task ReceiveFileAsync(
        TransferProtocolStream protocol,
        TransferMessage start,
        CancellationToken cancellationToken)
    {
        string fileId = start.FileId ?? string.Empty;
        string fileName = Path.GetFileName(start.FileName ?? string.Empty);
        long totalBytes = start.FileLength ?? -1;
        string? expectedHash = start.Sha256;

        if (string.IsNullOrWhiteSpace(fileId) ||
            string.IsNullOrWhiteSpace(fileName) ||
            totalBytes < 0 ||
            totalBytes > TransferProtocol.MaxFileSize)
        {
            await RejectFileAsync(
                protocol,
                fileId,
                "invalidFileMetadata",
                "The file metadata is invalid.",
                cancellationToken);

            return;
        }

        if (expectedHash is not null)
        {
            try
            {
                expectedHash =
                    TransferFileIntegrity.NormalizeHash(expectedHash);
            }
            catch (FormatException)
            {
                await RejectFileAsync(
                    protocol,
                    fileId,
                    "invalidHash",
                    "The supplied SHA-256 hash is invalid.",
                    cancellationToken);

                return;
            }
        }

        string partialPath =
            await _partialStore.GetPartialFilePathAsync(
                fileId,
                cancellationToken);

        TransferResumeRecord? record =
            await _resumeStore.GetByFileIdAsync(fileId);

        if (record is not null)
        {
            if (record.Direction != TransferDirection.Receiving ||
                record.TotalBytes != totalBytes ||
                !string.Equals(
                    record.FileName,
                    fileName,
                    StringComparison.Ordinal))
            {
                await RejectFileAsync(
                    protocol,
                    fileId,
                    "transferConflict",
                    "An incompatible transfer already uses this file ID.",
                    cancellationToken);

                return;
            }


            if (record.State == TransferFileState.Completed)
            {
                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.FileAccepted,
                        FileId = fileId,
                        Offset = totalBytes
                    },
                    cancellationToken);

                // فرستنده پس از دریافت FileAccepted با Offset نهایی،
                // پیام FileComplete را ارسال می‌کند.
                TransferMessage? completion =
                    await protocol.ReadMessageAsync(cancellationToken);

                if (completion is null ||
                    completion.Type != TransferMessageTypes.FileComplete ||
                    !string.Equals(
                        completion.FileId,
                        fileId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The repeated file completion message is invalid.");
                }

                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.FileVerified,
                        FileId = fileId,
                        Offset = totalBytes,
                        Sha256 = record.ActualSha256
                            ?? record.ExpectedSha256
                    },
                    cancellationToken);

                return;
            }
        }
        else
        {
            record = new TransferResumeRecord
            {
                FileId = fileId,
                SessionId = _currentConnection!.SessionId,
                FileName = fileName,
                PartialFilePath = partialPath,
                TotalBytes = totalBytes,
                PersistedBytes = 0,
                ExpectedSha256 = expectedHash,
                Direction = TransferDirection.Receiving,
                State = TransferFileState.Transferring
            };

            await _resumeStore.UpsertAsync(record);
        }

        long partialLength =
            await _partialStore.GetPartialFileLengthAsync(
                fileId,
                cancellationToken);

        if (partialLength < record.PersistedBytes)
        {
            await RejectFileAsync(
                protocol,
                fileId,
                "partialFileMissing",
                "The partial file is shorter than the saved checkpoint.",
                cancellationToken);

            return;
        }

        if (partialLength > record.PersistedBytes)
        {
            await using var repairStream =
                await _partialStore.OpenForResumeAsync(
                    fileId,
                    record.PersistedBytes,
                    cancellationToken);
        }

        record = record with
        {
            State = TransferFileState.Transferring,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _resumeStore.UpsertAsync(record);

        await protocol.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.FileAccepted,
                FileId = fileId,
                Offset = record.PersistedBytes
            },
            cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            TransferMessage? message =
                await protocol.ReadMessageAsync(cancellationToken);

            if (message is null)
            {
                await SaveInterruptedStateAsync(record);
                return;
            }

            if (!string.Equals(
                message.FileId,
                fileId,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The received message belongs to a different file.");
            }

            if (message.Type == TransferMessageTypes.ChunkStart)
            {
                long offset = message.Offset ?? -1;
                int length = message.ChunkLength ?? 0;

                var chunk = new TransferChunkInfo
                {
                    FileId = fileId,
                    Offset = offset,
                    Length = length
                };

                chunk.Validate(totalBytes);

                if (offset != record.PersistedBytes)
                {
                    throw new InvalidDataException(
                        "The chunk offset does not match the saved checkpoint.");
                }

                byte[] data = new byte[length];


                try
                {
                    await protocol.ReadExactlyAsync(
                        data.AsMemory(),
                        cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    await SaveInterruptedStateAsync(record);
                    throw;
                }
                catch (IOException)
                {
                    await SaveInterruptedStateAsync(record);
                    throw;
                }


                await _partialStore.WriteChunkAsync(
                    fileId,
                    offset,
                    data,
                    cancellationToken);

                record = record with
                {
                    PersistedBytes = offset + length,
                    State = TransferFileState.Transferring,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                // Save the checkpoint before acknowledging the chunk.
                await _resumeStore.UpsertAsync(record);

                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.ChunkAccepted,
                        FileId = fileId,
                        Offset = record.PersistedBytes
                    },
                    cancellationToken);

                ProgressChanged?.Invoke(
                    this,
                    new TransferProgressChangedEventArgs(
                        record,
                        0));

                continue;
            }

            if (message.Type == TransferMessageTypes.FileComplete)
            {
                if (record.PersistedBytes != totalBytes)
                {
                    await RejectFileAsync(
                        protocol,
                        fileId,
                        "incompleteFile",
                        "The received file is incomplete.",
                        cancellationToken);

                    return;
                }

                await VerifyAndPublishFileAsync(
                    protocol,
                    record,
                    cancellationToken);

                return;
            }

            if (message.Type == TransferMessageTypes.CancelFile)
            {
                record = record with
                {
                    State = TransferFileState.Cancelled,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                await _resumeStore.UpsertAsync(record);

                await _partialStore.DeletePartialFileAsync(
                    fileId,
                    cancellationToken);

                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.FileRejected,
                        FileId = fileId,
                        ErrorCode = "cancelled",
                        Message = "The transfer was cancelled."
                    },
                    cancellationToken);

                return;
            }

            throw new InvalidDataException(
                $"Unexpected message type during file transfer: {message.Type}");
        }
    }

    private async Task VerifyAndPublishFileAsync(
        TransferProtocolStream protocol,
        TransferResumeRecord record,
        CancellationToken cancellationToken)
    {
        string fileId = record.FileId;
        string fileName = record.FileName;

        await using var partial =
            await _partialStore.OpenForVerificationAsync(
                fileId,
                record.TotalBytes,
                cancellationToken);

        string actualHash =
            await TransferFileIntegrity.ComputeStreamHashAsync(
                partial,
                cancellationToken);

        if (record.ExpectedSha256 is not null &&
            !TransferFileIntegrity.HashesMatch(
                record.ExpectedSha256,
                actualHash))
        {
            TransferResumeRecord failed = record with
            {
                State = TransferFileState.Failed,
                ActualSha256 = actualHash,
                LastError = "SHA-256 verification failed.",
                UpdatedAtUtc = DateTime.UtcNow
            };

            await _resumeStore.UpsertAsync(failed);

            await protocol.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.FileRejected,
                    FileId = fileId,
                    ErrorCode = "hashMismatch",
                    Message = "The received file failed integrity verification."
                },
                cancellationToken);

            ErrorOccurred?.Invoke(
                this,
                new TransferErrorEventArgs(
                    "File integrity verification failed.",
                    fileId));

            return;
        }

        partial.Position = 0;

        ReceivedFileStorageService.StoredFileInfo stored =
            await _storage.SaveAsync(
                fileName,
                record.TotalBytes,
                partial,
                cancellationToken: cancellationToken);

        TransferResumeRecord completed = record with
        {
            PersistedBytes = record.TotalBytes,
            ActualSha256 = actualHash,
            FinalFilePath = stored.Location,
            State = TransferFileState.Completed,
            CompletedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = null
        };

        await _resumeStore.UpsertAsync(completed);

        await _partialStore.DeletePartialFileAsync(
            fileId,
            cancellationToken);

        FileReceived?.Invoke(
            this,
            new TransferFileReceivedEventArgs(
                fileId,
                stored.FileName,
                stored.Location,
                stored.Length));

        await protocol.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.FileVerified,
                FileId = fileId,
                Offset = record.TotalBytes,
                Sha256 = actualHash
            },
            cancellationToken);
    }


    public async Task QueueFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isReceiver)
        {
            throw new InvalidOperationException(
                "This session is operating as a receiver.");
        }

        ArgumentNullException.ThrowIfNull(filePaths);

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            EnsureConnected();

            // مرحله اول: آماده‌سازی همه فایل‌ها قبل از شروع انتقال
            var records = new List<TransferResumeRecord>();

            foreach (string path in filePaths.Distinct(
                StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                TransferFileDescriptor descriptor =
                    await TransferFileDescriptor.CreateAsync(
                        path,
                        calculateHash: true,
                        cancellationToken);

                descriptor.Validate();

                var record = new TransferResumeRecord
                {
                    FileId = descriptor.FileId,
                    SessionId = _currentConnection!.SessionId,
                    FileName = descriptor.FileName,
                    SourcePath = descriptor.SourcePath,
                    TotalBytes = descriptor.FileLength,
                    PersistedBytes = 0,
                    ExpectedSha256 = descriptor.Sha256,
                    Direction = TransferDirection.Sending,
                    State = TransferFileState.Queued
                };

                records.Add(record);
            }

            // مرحله دوم: ثبت همه فایل‌ها در صف پایدار
            foreach (TransferResumeRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _resumeStore.UpsertAsync(record);
                _queue.TryAdd(record);
            }

            // مرحله سوم: انتقال ترتیبی
            foreach (TransferResumeRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await SendExistingRecordAsync(
                        record,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await SaveSenderStateAsync(
                        record,
                        TransferFileState.Paused,
                        "Transfer was interrupted.");

                    throw;
                }
                catch (Exception ex)
                {
                    await SaveSenderStateAsync(
                        record,
                        TransferFileState.Paused,
                        ex.Message);

                    ErrorOccurred?.Invoke(
                        this,
                        new TransferErrorEventArgs(
                            "The file transfer was interrupted. " +
                            "Remaining files are still queued.",
                            record.FileId,
                            ex));

                    // انتقال فایل‌های بعدی تا رفع مشکل متوقف می‌شود؛
                    // اما اطلاعات آن‌ها در صف باقی می‌ماند.
                    break;
                }
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }


    private async Task SendExistingRecordAsync(
        TransferResumeRecord record,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(record.SourcePath) ||
            !File.Exists(record.SourcePath))
        {
            throw new FileNotFoundException(
                "The original source file is no longer available.",
                record.SourcePath);
        }

        await using var source = new FileStream(
            record.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (source.Length != record.TotalBytes)
        {
            throw new InvalidDataException(
                "The source file size changed after the transfer was created.");
        }

        await _protocol!.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.FileStart,
                SessionId = _currentConnection!.SessionId,
                FileId = record.FileId,
                FileName = record.FileName,
                FileLength = record.TotalBytes,
                Sha256 = record.ExpectedSha256
            },
            cancellationToken);

        TransferMessage? accepted =
            await _protocol.ReadMessageAsync(cancellationToken);

        if (accepted is null ||
            accepted.Type != TransferMessageTypes.FileAccepted ||
            accepted.FileId != record.FileId)
        {
            throw new IOException(
                accepted?.Message ?? "The receiver rejected the file.");
        }

        long offset = accepted.Offset ?? 0;

        if (offset < 0 || offset > record.TotalBytes)
        {
            throw new InvalidDataException(
                "The receiver returned an invalid resume offset.");
        }

        source.Position = offset;

        record = record with
        {
            PersistedBytes = offset,
            State = TransferFileState.Transferring,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _resumeStore.UpsertAsync(record);

        byte[] buffer = new byte[TransferProtocol.DefaultChunkSize];

        while (offset < record.TotalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(
                buffer.Length,
                record.TotalBytes - offset);

            int read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The source file ended before the expected length.");
            }

            await _protocol.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.ChunkStart,
                    FileId = record.FileId,
                    Offset = offset,
                    ChunkLength = read
                },
                cancellationToken);

            await _protocol.WriteExactlyAsync(
                buffer.AsMemory(0, read),
                cancellationToken);

            TransferMessage? acknowledgement =
                await _protocol.ReadMessageAsync(cancellationToken);

            long nextOffset = offset + read;

            if (acknowledgement is null ||
                acknowledgement.Type != TransferMessageTypes.ChunkAccepted ||
                acknowledgement.FileId != record.FileId ||
                acknowledgement.Offset != nextOffset)
            {
                throw new IOException(
                    "The receiver did not confirm the saved chunk.");
            }

            offset = nextOffset;

            record = record with
            {
                PersistedBytes = offset,
                State = TransferFileState.Transferring,
                UpdatedAtUtc = DateTime.UtcNow
            };

            await _resumeStore.UpsertAsync(record);

            ProgressChanged?.Invoke(
                this,
                new TransferProgressChangedEventArgs(record, 0));
        }

        await _protocol.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.FileComplete,
                FileId = record.FileId,
                Offset = record.TotalBytes,
                Sha256 = record.ExpectedSha256
            },
            cancellationToken);

        TransferMessage? verified =
            await _protocol.ReadMessageAsync(cancellationToken);

        if (verified is null ||
            verified.Type != TransferMessageTypes.FileVerified ||
            verified.FileId != record.FileId)
        {
            throw new IOException(
                verified?.Message ??
                "The receiver did not verify the completed file.");
        }

        TransferResumeRecord completed = record with
        {
            PersistedBytes = record.TotalBytes,
            ActualSha256 = verified.Sha256,
            State = TransferFileState.Completed,
            CompletedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = null
        };

        await _resumeStore.UpsertAsync(completed);
        _queue.TryUpdate(completed);
    }

    public async Task ResumeAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        TransferResumeRecord? record =
            await _resumeStore.GetByFileIdAsync(fileId);

        if (record is null)
        {
            throw new InvalidOperationException(
                "The transfer record was not found.");
        }

        if (record.Direction != TransferDirection.Sending)
        {
            throw new InvalidOperationException(
                "Only outgoing transfers can be resumed from this device.");
        }

        if (record.State == TransferFileState.Completed)
        {
            return;
        }

        if (State != TransferSessionState.Connected)
        {
            TransferConnectionInfo? previous = _currentConnection;

            if (previous is null)
            {
                throw new InvalidOperationException(
                    "Reconnect using the receiver's QR code before resuming.");
            }

            await ConnectAsync(
                previous.IpAddress,
                previous.Port,
                previous.Token,
                cancellationToken: cancellationToken);
        }

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            await SendExistingRecordAsync(record, cancellationToken);
        }
        catch (Exception ex)
        {
            await SaveSenderStateAsync(
                record,
                TransferFileState.Paused,
                ex.Message);

            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task CancelAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        TransferResumeRecord? record =
            await _resumeStore.GetByFileIdAsync(fileId);

        if (record is null)
        {
            return;
        }

        if (record.State == TransferFileState.Transferring)
        {
            throw new InvalidOperationException(
                "An active transfer cannot yet be safely cancelled by this " +
                "initial engine. Disconnect the session to interrupt it; " +
                "the saved checkpoint can then be resumed.");
        }

        TransferResumeRecord cancelled = record with
        {
            State = TransferFileState.Cancelled,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _resumeStore.UpsertAsync(cancelled);
        _queue.TryUpdate(cancelled);

        if (record.Direction == TransferDirection.Receiving)
        {
            await _partialStore.DeletePartialFileAsync(
                fileId,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TransferResumeRecord>> GetTransfersAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TransferResumeRecord> records =
            await _resumeStore.GetAllAsync();

        cancellationToken.ThrowIfCancellationRequested();

        _queue.Load(records);

        return records;
    }


    public async Task DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        // وضعیت را پیش از تغییر ذخیره می‌کنیم.
        bool wasConnected = State == TransferSessionState.Connected;
        TransferProtocolStream? protocol = _protocol;
        string? sessionId = _currentConnection?.SessionId;

        SetState(TransferSessionState.Closing, "Closing the session.");

        if (wasConnected && protocol is not null)
        {
            try
            {
                await protocol.WriteMessageAsync(
                    new TransferMessage
                    {
                        Type = TransferMessageTypes.EndSession,
                        SessionId = sessionId
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is SocketException ||
                ex is ObjectDisposedException ||
                ex is OperationCanceledException)
            {
                // اتصال ممکن است قبلاً قطع شده باشد.
            }
        }

        _lifetime?.Cancel();
        _listener?.Stop();

        _client?.Dispose();
        _client = null;
        _protocol = null;

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
                // خاموش‌شدن عادی نشست
            }
            catch (ObjectDisposedException)
            {
                // Listener قبلاً بسته شده است
            }
        }

        _listener = null;
        _acceptTask = null;

        _lifetime?.Dispose();
        _lifetime = null;

        _isReceiver = false;
        _currentConnection = null;

        SetState(TransferSessionState.Disconnected, "Disconnected.");
    }


    private async Task SaveSenderStateAsync(
        TransferResumeRecord record,
        TransferFileState state,
        string? error)
    {
        // رکورد ذخیره‌شده ممکن است پیشرفت بیشتری از رکورد ورودی داشته باشد.
        TransferResumeRecord? latest =
            await _resumeStore.GetByFileIdAsync(record.FileId);

        TransferResumeRecord current = latest ?? record;

        TransferResumeRecord updated = current with
        {
            State = state,
            LastError = error,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _resumeStore.UpsertAsync(updated);
        _queue.TryUpdate(updated);
    }


    private async Task SaveInterruptedStateAsync(
        TransferResumeRecord record)
    {
        TransferResumeRecord paused = record with
        {
            State = TransferFileState.Paused,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _resumeStore.UpsertAsync(paused);
    }

    private static async Task RejectFileAsync(
        TransferProtocolStream protocol,
        string fileId,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        await protocol.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.FileRejected,
                FileId = fileId,
                ErrorCode = errorCode,
                Message = message
            },
            cancellationToken);
    }

    private void EnsureConnected()
    {
        if (State != TransferSessionState.Connected ||
            _client is null ||
            _protocol is null ||
            !_client.Connected)
        {
            throw new InvalidOperationException(
                "No active transfer connection exists.");
        }
    }

    private void SetState(
        TransferSessionState newState,
        string? message = null)
    {
        TransferSessionState previous;

        lock (_stateLock)
        {
            previous = _state;
            _state = newState;
        }

        if (previous != newState)
        {
            SessionStateChanged?.Invoke(
                this,
                new TransferSessionStateChangedEventArgs(
                    previous,
                    newState,
                    message));
        }
    }

    private static string GetDeviceName(string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName.Trim();
        }

        return Environment.MachineName;
    }

    private static IPAddress? GetLocalIPv4Address()
    {
        foreach (NetworkInterface networkInterface
                 in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus !=
                OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties properties =
                networkInterface.GetIPProperties();

            foreach (UnicastIPAddressInformation address
                     in properties.UnicastAddresses)
            {
                IPAddress ip = address.Address;

                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip))
                {
                    return ip;
                }
            }
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync();

        _disposed = true;

        _operationLock.Dispose();
    }
}
