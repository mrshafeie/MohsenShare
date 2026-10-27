
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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

    private readonly ConcurrentDictionary<string, TransferDirection>
        _activeTransfers = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte>
        _cancelRequests = new(StringComparer.Ordinal);

    private TcpListener? _listener;
    private TcpClient? _client;
    private TransferProtocolStream? _protocol;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;

    private TransferConnectionInfo? _currentConnection;
    private TransferSessionState _state =
        TransferSessionState.Disconnected;

    private bool _isReceiver;
    private volatile bool _disposed;

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
                return _state;
        }
    }

    public TransferConnectionInfo? CurrentConnection =>
        _currentConnection;

    // ---------------------------------------------------------
    // Receiver startup
    // ---------------------------------------------------------

    public Task<TransferConnectionInfo> StartListeningAsync(
        string? localDeviceName = null,
        IPAddress? preferredAddress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_listener is not null || _client is not null)
        {
            throw new InvalidOperationException(
                "A transfer connection or listener is already active.");
        }

        IPAddress address = preferredAddress ?? GetLocalIPv4Address()
            ?? throw new InvalidOperationException(
                "No suitable local IPv4 address was found.");

        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "A reachable local IPv4 address is required.",
                nameof(preferredAddress));
        }

        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();

        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _lifetime?.Dispose();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);

        _isReceiver = true;

        _currentConnection = new TransferConnectionInfo
        {
            SessionId = Guid.NewGuid().ToString("N"),
            DeviceName = GetDeviceName(localDeviceName),
            IpAddress = address.ToString(),
            Port = port,
            Token = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32))
        };

        SetState(
            TransferSessionState.Connecting,
            "Waiting for a device to connect.");

        _acceptTask = AcceptLoopAsync(_lifetime.Token);

        return Task.FromResult(_currentConnection);
    }

    // ---------------------------------------------------------
    // Sender connection
    // ---------------------------------------------------------

    public async Task ConnectAsync(
        string ipAddress,
        int port,
        string token,
        string? localDeviceName = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_listener is not null)
        {
            throw new InvalidOperationException(
                "Stop the receiver listener before connecting as a sender.");
        }

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
            throw new ArgumentOutOfRangeException(nameof(port));

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException(
                "The connection token is required.",
                nameof(token));
        }

        SetState(
            TransferSessionState.Connecting,
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
                    response?.Message ??
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

            SetState(
                TransferSessionState.Connected,
                "Connected to the receiving device.");
        }
        catch
        {
            client.Dispose();

            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _protocol = null;
            }

            SetState(
                TransferSessionState.Failed,
                "Could not establish the connection.");

            throw;
        }
    }

    // ---------------------------------------------------------
    // Accept loop
    // ---------------------------------------------------------

    private async Task AcceptLoopAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener!
                    .AcceptTcpClientAsync(cancellationToken);

                if (_client is not null)
                {
                    client.Dispose();
                    continue;
                }

                _client = client;
                var protocol = new TransferProtocolStream(
                    client.GetStream());

                _protocol = protocol;

                try
                {
                    await ProcessReceiverConnectionAsync(
                        protocol,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RaiseError(
                        "The receiver connection ended unexpectedly.",
                        exception: ex);
                }
                finally
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                        _protocol = null;
                    }

                    client.Dispose();

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SetState(
                            TransferSessionState.Connecting,
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
            // Listener was stopped.
        }
        catch (SocketException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Listener was stopped during accept.
        }
        catch (Exception ex)
        {
            SetState(TransferSessionState.Failed, ex.Message);
            RaiseError(
                "The receiver could not continue listening.",
                exception: ex);
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
            await RejectFileAsync(
                protocol,
                string.Empty,
                "invalidHandshake",
                "The connection token is invalid.",
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

        SetState(
            TransferSessionState.Connected,
            "A device connected.");

        while (!cancellationToken.IsCancellationRequested)
        {
            TransferMessage? message =
                await protocol.ReadMessageAsync(cancellationToken);

            if (message is null)
                break;

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

                case TransferMessageTypes.EndSession:
                    await protocol.WriteMessageAsync(
                        new TransferMessage
                        {
                            Type = TransferMessageTypes.SessionEnded,
                            SessionId = _currentConnection.SessionId
                        },
                        cancellationToken);

                    return;

                default:
                    await protocol.WriteMessageAsync(
                        new TransferMessage
                        {
                            Type = TransferMessageTypes.Error,
                            ErrorCode = "unsupportedMessage",
                            Message = "Unsupported message type."
                        },
                        cancellationToken);
                    break;
            }
        }
    }

    // ---------------------------------------------------------
    // Receive and resume a file
    // ---------------------------------------------------------

    private async Task ReceiveFileAsync(
        TransferProtocolStream protocol,
        TransferMessage start,
        CancellationToken cancellationToken)
    {
        string fileId = start.FileId ?? string.Empty;
        string fileName = Path.GetFileName(
            start.FileName ?? string.Empty);

        long totalBytes = start.FileLength ?? -1;
        string? expectedHash = start.Sha256;

        if (!Guid.TryParseExact(fileId, "N", out _) ||
            string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
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

        TransferResumeRecord? existing =
            await _resumeStore.GetByFileIdAsync(
                fileId,
                cancellationToken);

        if (existing?.State == TransferFileState.Completed)
        {
            if (existing.TotalBytes != totalBytes ||
                !string.Equals(
                    existing.FileName,
                    fileName,
                    StringComparison.Ordinal) ||
                (expectedHash is not null &&
                 existing.ExpectedSha256 is not null &&
                 !TransferFileIntegrity.HashesMatch(
                     expectedHash,
                     existing.ExpectedSha256)))
            {
                await RejectFileAsync(
                    protocol,
                    fileId,
                    "transferConflict",
                    "The completed transfer metadata does not match.",
                    cancellationToken);
                return;
            }

            // The previous FileVerified response may have been lost.
            await protocol.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.FileAccepted,
                    FileId = fileId,
                    Offset = totalBytes
                },
                cancellationToken);

            TransferMessage? completion =
                await protocol.ReadMessageAsync(cancellationToken);

            if (completion?.Type != TransferMessageTypes.FileComplete ||
                completion.FileId != fileId)
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
                    Sha256 = existing.ActualSha256
                        ?? existing.ExpectedSha256
                },
                cancellationToken);

            return;
        }

        if (existing?.State == TransferFileState.Cancelled)
        {
            await RejectFileAsync(
                protocol,
                fileId,
                "cancelled",
                "This transfer has already been cancelled.",
                cancellationToken);
            return;
        }

        if (existing is not null &&
            (existing.Direction != TransferDirection.Receiving ||
             existing.TotalBytes != totalBytes ||
             !string.Equals(
                 existing.FileName,
                 fileName,
                 StringComparison.Ordinal) ||
             (expectedHash is not null &&
              existing.ExpectedSha256 is not null &&
              !TransferFileIntegrity.HashesMatch(
                  expectedHash,
                  existing.ExpectedSha256))))
        {
            await RejectFileAsync(
                protocol,
                fileId,
                "transferConflict",
                "An incompatible transfer already uses this file ID.",
                cancellationToken);
            return;
        }

        string partialPath =
            await _partialStore.GetPartialFilePathAsync(
                fileId,
                cancellationToken);

        TransferResumeRecord record = existing ?? new TransferResumeRecord
        {
            FileId = fileId,
            SessionId = _currentConnection!.SessionId,
            FileName = fileName,
            PartialFilePath = partialPath,
            TotalBytes = totalBytes,
            PersistedBytes = 0,
            ExpectedSha256 = expectedHash,
            Direction = TransferDirection.Receiving,
            State = TransferFileState.Queued
        };

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
                "The partial file is shorter than its saved checkpoint.",
                cancellationToken);
            return;
        }

        // Ensure the partial file exists and discard unconfirmed bytes.
        await using (FileStream resumeStream =
            await _partialStore.OpenForResumeAsync(
                fileId,
                record.PersistedBytes,
                cancellationToken))
        {
            await resumeStream.FlushAsync(cancellationToken);
        }

        record = record with
        {
            SessionId = _currentConnection!.SessionId,
            PartialFilePath = partialPath,
            ExpectedSha256 = expectedHash ?? record.ExpectedSha256,
            State = TransferFileState.Transferring,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = null
        };

        await _resumeStore.UpsertAsync(record);
        UpdateQueue(record);

        _activeTransfers[fileId] = TransferDirection.Receiving;
        var stopwatch = Stopwatch.StartNew();

        try
        {
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
                TransferMessage? message;

                try
                {
                    message = await protocol.ReadMessageAsync(
                        cancellationToken);
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is SocketException ||
                    ex is ObjectDisposedException ||
                    ex is OperationCanceledException)
                {
                    await SaveInterruptedStateAsync(record);
                    throw;
                }

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

                if (message.Type == TransferMessageTypes.CancelFile)
                {
                    await MarkCancelledAsync(
                        record,
                        protocol,
                        cancellationToken);
                    return;
                }

                if (message.Type == TransferMessageTypes.ChunkStart)
                {
                    var chunk = new TransferChunkInfo
                    {
                        FileId = fileId,
                        Offset = message.Offset ?? -1,
                        Length = message.ChunkLength ?? 0
                    };

                    chunk.Validate(totalBytes);

                    if (chunk.Offset != record.PersistedBytes)
                    {
                        throw new InvalidDataException(
                            "The chunk offset does not match the saved checkpoint.");
                    }

                    byte[] data = new byte[chunk.Length];

                    try
                    {
                        await protocol.ReadExactlyAsync(
                            data.AsMemory(),
                            cancellationToken);
                    }
                    catch (Exception ex) when (
                        ex is IOException ||
                        ex is SocketException ||
                        ex is ObjectDisposedException ||
                        ex is OperationCanceledException)
                    {
                        await SaveInterruptedStateAsync(record);
                        throw;
                    }

                    await _partialStore.WriteChunkAsync(
                        fileId,
                        chunk.Offset,
                        data,
                        cancellationToken);

                    // Persist the checkpoint before acknowledging the chunk.
                    record = record with
                    {
                        PersistedBytes = chunk.EndOffset,
                        State = TransferFileState.Transferring,
                        UpdatedAtUtc = DateTime.UtcNow,
                        LastError = null
                    };

                    await _resumeStore.UpsertAsync(record);
                    UpdateQueue(record);

                    if (_cancelRequests.TryRemove(fileId, out _))
                    {
                        await MarkCancelledAsync(
                            record,
                            protocol,
                            cancellationToken,
                            sendRejection: true);
                        return;
                    }

                    await protocol.WriteMessageAsync(
                        new TransferMessage
                        {
                            Type = TransferMessageTypes.ChunkAccepted,
                            FileId = fileId,
                            Offset = record.PersistedBytes
                        },
                        cancellationToken);

                    RaiseProgress(record, stopwatch);
                    continue;
                }

                if (message.Type == TransferMessageTypes.FileComplete)
                {
                    if (_cancelRequests.TryRemove(fileId, out _))
                    {
                        await MarkCancelledAsync(
                            record,
                            protocol,
                            cancellationToken);
                        return;
                    }

                    if (record.PersistedBytes != totalBytes ||
                        (message.Offset.HasValue &&
                         message.Offset.Value != totalBytes))
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

                throw new InvalidDataException(
                    $"Unexpected message type during file transfer: {message.Type}");
            }
        }
        catch (OperationCanceledException)
        {
            await SaveInterruptedStateAsync(record);
            throw;
        }
        catch (IOException)
        {
            await SaveInterruptedStateAsync(record);
            throw;
        }
        finally
        {
            _activeTransfers.TryRemove(fileId, out _);
            _cancelRequests.TryRemove(fileId, out _);
        }
    }

    private async Task VerifyAndPublishFileAsync(
        TransferProtocolStream protocol,
        TransferResumeRecord record,
        CancellationToken cancellationToken)
    {
        string fileId = record.FileId;

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
                PersistedBytes = 0,
                ActualSha256 = actualHash,
                State = TransferFileState.Failed,
                LastError = "SHA-256 verification failed.",
                UpdatedAtUtc = DateTime.UtcNow
            };

            await _partialStore.DeletePartialFileAsync(
                fileId,
                cancellationToken);

            await _resumeStore.UpsertAsync(failed);
            UpdateQueue(failed);

            await RejectFileAsync(
                protocol,
                fileId,
                "hashMismatch",
                "The received file failed integrity verification.",
                cancellationToken);

            RaiseError("File integrity verification failed.", fileId);
            return;
        }

        partial.Position = 0;

        ReceivedFileStorageService.StoredFileInfo stored =
            await _storage.SaveAsync(
                record.FileName,
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
        UpdateQueue(completed);

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

        RaiseProgress(completed, Stopwatch.StartNew());

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

    // ---------------------------------------------------------
    // Persistent multi-file queue
    // ---------------------------------------------------------

    public async Task QueueFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePaths);

        if (_isReceiver)
        {
            throw new InvalidOperationException(
                "This session is operating as a receiver.");
        }

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            EnsureConnected();

            var records = new List<TransferResumeRecord>();
            var uniquePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            // Prepare every file before beginning the batch.
            foreach (string path in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(path))
                    continue;

                string fullPath = Path.GetFullPath(path);

                if (!uniquePaths.Add(fullPath))
                    continue;

                TransferFileDescriptor descriptor =
                    await TransferFileDescriptor.CreateAsync(
                        fullPath,
                        calculateHash: true,
                        cancellationToken);

                descriptor.Validate();

                records.Add(new TransferResumeRecord
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
                });
            }

            // Persist the whole queue before sending the first file.
            foreach (TransferResumeRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _resumeStore.UpsertAsync(record, cancellationToken);
                UpdateQueue(record);
            }

            foreach (TransferResumeRecord original in records)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TransferResumeRecord? latest =
                    await _resumeStore.GetByFileIdAsync(
                        original.FileId,
                        cancellationToken);

                if (latest?.State == TransferFileState.Cancelled)
                    continue;

                try
                {
                    await SendExistingRecordAsync(
                        latest ?? original,
                        cancellationToken);

                    TransferResumeRecord? result =
                        await _resumeStore.GetByFileIdAsync(
                            original.FileId,
                            cancellationToken);

                    if (result?.State == TransferFileState.Failed)
                        break;
                }
                catch (OperationCanceledException)
                {
                    await SaveSenderStateAsync(
                        original,
                        TransferFileState.Paused,
                        "The transfer queue was interrupted.");

                    throw;
                }
                catch (Exception ex)
                {
                    await SaveSenderStateAsync(
                        original,
                        TransferFileState.Paused,
                        ex.Message);

                    RaiseError(
                        "The file transfer was interrupted. " +
                        "Remaining files are still queued.",
                        original.FileId,
                        ex);

                    // Remaining records are already persisted.
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
        TransferResumeRecord initialRecord,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        TransferResumeRecord record = initialRecord;

        if (record.State == TransferFileState.Completed ||
            record.State == TransferFileState.Cancelled)
        {
            return;
        }

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

        string currentHash =
            await TransferFileIntegrity.ComputeFileHashAsync(
                record.SourcePath,
                cancellationToken);

        if (record.ExpectedSha256 is not null &&
            !TransferFileIntegrity.HashesMatch(
                record.ExpectedSha256,
                currentHash))
        {
            throw new InvalidDataException(
                "The source file content changed after it was queued.");
        }

        _activeTransfers[record.FileId] = TransferDirection.Sending;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            record = record with
            {
                SessionId = _currentConnection!.SessionId,
                State = TransferFileState.Transferring,
                UpdatedAtUtc = DateTime.UtcNow,
                LastError = null
            };

            await _resumeStore.UpsertAsync(record);
            UpdateQueue(record);

            await _protocol!.WriteMessageAsync(
                new TransferMessage
                {
                    Type = TransferMessageTypes.FileStart,
                    SessionId = _currentConnection.SessionId,
                    FileId = record.FileId,
                    FileName = record.FileName,
                    FileLength = record.TotalBytes,
                    Sha256 = record.ExpectedSha256
                },
                cancellationToken);

            TransferMessage? accepted =
                await _protocol.ReadMessageAsync(cancellationToken);

            if (accepted?.Type == TransferMessageTypes.FileRejected)
            {
                await HandleRejectionAsync(record, accepted);
                return;
            }

            if (accepted?.Type != TransferMessageTypes.FileAccepted ||
                accepted.FileId != record.FileId)
            {
                throw new IOException(
                    accepted?.Message ??
                    "The receiver did not accept the file.");
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
            UpdateQueue(record);
            RaiseProgress(record, stopwatch);

            byte[] buffer = new byte[TransferProtocol.DefaultChunkSize];

            while (offset < record.TotalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_cancelRequests.TryRemove(record.FileId, out _))
                {
                    await SendCancelRequestAsync(
                        record,
                        cancellationToken);
                    return;
                }

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

                if (acknowledgement?.Type == TransferMessageTypes.FileRejected)
                {
                    await HandleRejectionAsync(record, acknowledgement);
                    return;
                }

                long nextOffset = offset + read;

                if (acknowledgement?.Type !=
                        TransferMessageTypes.ChunkAccepted ||
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
                    UpdatedAtUtc = DateTime.UtcNow,
                    LastError = null
                };

                await _resumeStore.UpsertAsync(record);
                UpdateQueue(record);
                RaiseProgress(record, stopwatch);

                // Cancellation is coordinated at a chunk boundary.
                if (_cancelRequests.TryRemove(record.FileId, out _))
                {
                    await SendCancelRequestAsync(
                        record,
                        cancellationToken);
                    return;
                }
            }

            if (_cancelRequests.TryRemove(record.FileId, out _))
            {
                await SendCancelRequestAsync(
                    record,
                    cancellationToken);
                return;
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

            if (verified?.Type == TransferMessageTypes.FileRejected)
            {
                await HandleRejectionAsync(record, verified);
                return;
            }

            if (verified?.Type != TransferMessageTypes.FileVerified ||
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
            UpdateQueue(completed);
            RaiseProgress(completed, stopwatch);
        }
        finally
        {
            _activeTransfers.TryRemove(record.FileId, out _);
            _cancelRequests.TryRemove(record.FileId, out _);
        }
    }

    private async Task SendCancelRequestAsync(
        TransferResumeRecord record,
        CancellationToken cancellationToken)
    {
        await _protocol!.WriteMessageAsync(
            new TransferMessage
            {
                Type = TransferMessageTypes.CancelFile,
                FileId = record.FileId
            },
            cancellationToken);

        TransferMessage? response =
            await _protocol.ReadMessageAsync(cancellationToken);

        if (response?.Type == TransferMessageTypes.FileRejected &&
            string.Equals(
                response.ErrorCode,
                "cancelled",
                StringComparison.OrdinalIgnoreCase))
        {
            await MarkRecordCancelledAsync(
                record,
                deletePartial: false,
                cancellationToken);
            return;
        }

        throw new IOException(
            response?.Message ??
            "The receiver did not confirm cancellation.");
    }

    private async Task HandleRejectionAsync(
        TransferResumeRecord record,
        TransferMessage rejection)
    {
        bool cancelled = string.Equals(
            rejection.ErrorCode,
            "cancelled",
            StringComparison.OrdinalIgnoreCase);

        TransferFileState state = cancelled
            ? TransferFileState.Cancelled
            : TransferFileState.Failed;

        TransferResumeRecord updated = record with
        {
            State = state,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = rejection.Message ?? rejection.ErrorCode
        };

        await _resumeStore.UpsertAsync(updated);
        UpdateQueue(updated);

        if (!cancelled)
        {
            RaiseError(
                rejection.Message ?? "The receiver rejected the file.",
                record.FileId);
        }
    }

    // ---------------------------------------------------------
    // Resume and cancellation API
    // ---------------------------------------------------------

    public async Task ResumeAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        TransferResumeRecord? record =
            await _resumeStore.GetByFileIdAsync(
                fileId,
                cancellationToken);

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
            return;

        if (record.State == TransferFileState.Cancelled)
        {
            throw new InvalidOperationException(
                "A cancelled transfer cannot be resumed.");
        }

        if (State != TransferSessionState.Connected)
        {
            TransferConnectionInfo? previous = _currentConnection;

            if (previous is null)
            {
                throw new InvalidOperationException(
                    "Reconnect using the receiver's current QR code first.");
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
            record = await _resumeStore.GetByFileIdAsync(
                fileId,
                cancellationToken) ?? record;

            await SendExistingRecordAsync(record, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await SaveSenderStateAsync(
                record,
                TransferFileState.Paused,
                "The transfer was interrupted.");

            throw;
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
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        TransferResumeRecord? record =
            await _resumeStore.GetByFileIdAsync(
                fileId,
                cancellationToken);

        if (record is null ||
            record.State is TransferFileState.Completed
                or TransferFileState.Cancelled)
        {
            return;
        }

        if (_activeTransfers.ContainsKey(fileId))
        {
            // The active operation observes this request at a safe
            // protocol boundary and coordinates with the other endpoint.
            _cancelRequests[fileId] = 0;
            return;
        }

        await MarkRecordCancelledAsync(
            record,
            deletePartial: record.Direction == TransferDirection.Receiving,
            cancellationToken);
    }

    private async Task MarkCancelledAsync(
        TransferResumeRecord record,
        TransferProtocolStream protocol,
        CancellationToken cancellationToken,
        bool sendRejection = true)
    {
        await MarkRecordCancelledAsync(
            record,
            deletePartial: true,
            cancellationToken);

        if (sendRejection)
        {
            await RejectFileAsync(
                protocol,
                record.FileId,
                "cancelled",
                "The transfer was cancelled.",
                cancellationToken);
        }
    }

    private async Task MarkRecordCancelledAsync(
        TransferResumeRecord record,
        bool deletePartial,
        CancellationToken cancellationToken)
    {
        TransferResumeRecord cancelled = record with
        {
            State = TransferFileState.Cancelled,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = "The transfer was cancelled."
        };

        await _resumeStore.UpsertAsync(
            cancelled,
            cancellationToken);

        UpdateQueue(cancelled);

        if (deletePartial &&
            record.Direction == TransferDirection.Receiving)
        {
            await _partialStore.DeletePartialFileAsync(
                record.FileId,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TransferResumeRecord>> GetTransfersAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        IReadOnlyList<TransferResumeRecord> records =
            await _resumeStore.GetAllAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        _queue.Load(records);

        return records;
    }

    // ---------------------------------------------------------
    // State persistence
    // ---------------------------------------------------------

    private async Task SaveSenderStateAsync(
        TransferResumeRecord record,
        TransferFileState state,
        string? error)
    {
        TransferResumeRecord? latest =
            await _resumeStore.GetByFileIdAsync(record.FileId);

        TransferResumeRecord current = latest ?? record;

        if (current.State is TransferFileState.Completed
            or TransferFileState.Cancelled)
        {
            return;
        }

        TransferResumeRecord updated = current with
        {
            State = state,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = error
        };

        await _resumeStore.UpsertAsync(updated);
        UpdateQueue(updated);
    }

    private async Task SaveInterruptedStateAsync(
        TransferResumeRecord record)
    {
        TransferResumeRecord? latest =
            await _resumeStore.GetByFileIdAsync(record.FileId);

        TransferResumeRecord current = latest ?? record;

        if (current.State is TransferFileState.Completed
            or TransferFileState.Cancelled)
        {
            return;
        }

        TransferResumeRecord paused = current with
        {
            State = TransferFileState.Paused,
            UpdatedAtUtc = DateTime.UtcNow,
            LastError = "Connection interrupted. Resume from the last confirmed checkpoint."
        };

        await _resumeStore.UpsertAsync(paused);
        UpdateQueue(paused);
    }

    private void UpdateQueue(TransferResumeRecord record)
    {
        if (!_queue.TryUpdate(record))
            _queue.TryAdd(record);
    }

    // ---------------------------------------------------------
    // Disconnect and disposal
    // ---------------------------------------------------------

    public async Task DisconnectAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        bool wasConnected =
            State == TransferSessionState.Connected;

        TransferProtocolStream? protocol = _protocol;
        string? sessionId = _currentConnection?.SessionId;

        SetState(
            TransferSessionState.Closing,
            "Closing the session.");

        // Do not send control messages while a file is in the middle
        // of its binary payload. Closing the socket preserves the
        // last persisted checkpoint instead.
        if (wasConnected &&
            protocol is not null &&
            _activeTransfers.IsEmpty)
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
            catch (IOException)
            {
                // Connection may already be closed.
            }
            catch (SocketException)
            {
                // Connection may already be closed.
            }
            catch (ObjectDisposedException)
            {
                // Stream was disposed.
            }
            catch (OperationCanceledException)
            {
                // Shutdown was cancelled.
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
                // Expected during shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Listener was already stopped.
            }
        }

        _listener = null;
        _acceptTask = null;

        _lifetime?.Dispose();
        _lifetime = null;

        _isReceiver = false;

        // Keep connection details available so the sender can retry.
        SetState(
            TransferSessionState.Disconnected,
            "Disconnected.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await DisconnectAsync();

        _disposed = true;
        _operationLock.Dispose();
    }

    // ---------------------------------------------------------
    // Utilities
    // ---------------------------------------------------------

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

    private void RaiseProgress(
        TransferResumeRecord record,
        Stopwatch stopwatch)
    {
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

        long speed = elapsedSeconds > 0
            ? (long)(record.PersistedBytes / elapsedSeconds)
            : 0;

        long remaining = Math.Max(
            0,
            record.TotalBytes - record.PersistedBytes);

        TimeSpan? eta = speed > 0
            ? TimeSpan.FromSeconds(remaining / (double)speed)
            : null;

        ProgressChanged?.Invoke(
            this,
            new TransferProgressChangedEventArgs(
                record,
                speed,
                eta));
    }

    private void RaiseError(
        string message,
        string? fileId = null,
        Exception? exception = null)
    {
        ErrorOccurred?.Invoke(
            this,
            new TransferErrorEventArgs(
                message,
                fileId,
                exception));
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

    private static string GetDeviceName(string? requestedName)
    {
        return string.IsNullOrWhiteSpace(requestedName)
            ? Environment.MachineName
            : requestedName.Trim();
    }

    private static IPAddress? GetLocalIPv4Address()
    {
        var candidates = new List<IPAddress>();

        foreach (NetworkInterface networkInterface
                 in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus !=
                OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties properties;

            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast
                     in properties.UnicastAddresses)
            {
                IPAddress address = unicast.Address;

                if (address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address))
                {
                    continue;
                }

                byte[] bytes = address.GetAddressBytes();

                // Skip IPv4 link-local addresses.
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;

                candidates.Add(address);
            }
        }

        // Prefer private LAN addresses for local transfer.
        return candidates.FirstOrDefault(IsPrivateIPv4)
            ?? candidates.FirstOrDefault();
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();

        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
