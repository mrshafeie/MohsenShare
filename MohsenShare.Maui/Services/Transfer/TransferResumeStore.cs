
using System.Text.Json;

namespace MohsenShare.Maui.Services.Transfer;

/// <summary>
/// ذخیره پایدار وضعیت انتقال‌ها برای بازیابی پس از راه‌اندازی مجدد برنامه.
/// این سرویس فقط metadata را ذخیره می‌کند، نه محتوای فایل‌ها را.
/// </summary>
public sealed class TransferResumeStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record StoreDocument
    {
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public List<TransferResumeRecord> Transfers { get; init; } = [];
    }

    public TransferResumeStore()
    {
        _storePath = Path.Combine(
            FileSystem.Current.AppDataDirectory,
            "TransferState",
            "transfers.json");
    }

    /// <summary>
    /// تمام رکوردهای ذخیره‌شده را برمی‌گرداند.
    /// رکوردهای نامعتبر نادیده گرفته می‌شوند.
    /// </summary>
    public async Task<IReadOnlyList<TransferResumeRecord>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            StoreDocument document =
                await ReadDocumentUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);

            return document.Transfers
                .Where(IsValidRecord)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransferResumeRecord?> GetByFileIdAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        IReadOnlyList<TransferResumeRecord> records =
            await GetAllAsync(cancellationToken).ConfigureAwait(false);

        return records.FirstOrDefault(
            x => string.Equals(
                x.FileId,
                fileId,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// رکورد جدید را اضافه می‌کند یا رکورد هم‌شناسه را جایگزین می‌کند.
    /// </summary>
    public async Task UpsertAsync(
        TransferResumeRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            StoreDocument document =
                await ReadDocumentUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);

            var transfers = document.Transfers
                .Where(IsValidRecord)
                .Where(x => !string.Equals(
                    x.FileId,
                    record.FileId,
                    StringComparison.Ordinal))
                .ToList();

            transfers.Add(record with
            {
                UpdatedAtUtc = DateTime.UtcNow
            });

            await WriteDocumentUnsafeAsync(
                new StoreDocument
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Transfers = transfers
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            StoreDocument document =
                await ReadDocumentUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);

            List<TransferResumeRecord> remaining = document.Transfers
                .Where(IsValidRecord)
                .Where(x => !string.Equals(
                    x.FileId,
                    fileId,
                    StringComparison.Ordinal))
                .ToList();

            if (remaining.Count == document.Transfers.Count)
                return false;

            await WriteDocumentUnsafeAsync(
                new StoreDocument
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Transfers = remaining
                },
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// رکوردهای قدیمی‌تر از زمان مشخص را پاک می‌کند.
    /// فایل‌های فیزیکی را حذف نمی‌کند.
    /// </summary>
    public async Task<int> RemoveOlderThanAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            StoreDocument document =
                await ReadDocumentUnsafeAsync(cancellationToken)
                    .ConfigureAwait(false);

            List<TransferResumeRecord> remaining = document.Transfers
                .Where(IsValidRecord)
                .Where(x => x.UpdatedAtUtc >= cutoffUtc)
                .ToList();

            int removed = document.Transfers.Count - remaining.Count;

            if (removed > 0)
            {
                await WriteDocumentUnsafeAsync(
                    new StoreDocument
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Transfers = remaining
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreDocument> ReadDocumentUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
            return new StoreDocument();

        try
        {
            await using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            StoreDocument? document =
                await JsonSerializer.DeserializeAsync<StoreDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

            if (document is null ||
                document.SchemaVersion != CurrentSchemaVersion)
            {
                return new StoreDocument();
            }

            return document;
        }
        catch (JsonException)
        {
            // فایل خراب را بی‌صدا روی فایل اصلی بازنویسی نمی‌کنیم.
            // در این وضعیت، نسخه پشتیبان بررسی می‌شود.
            return await ReadBackupAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await ReadBackupAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<StoreDocument> ReadBackupAsync(
        CancellationToken cancellationToken)
    {
        string backupPath = _storePath + ".bak";

        if (!File.Exists(backupPath))
            return new StoreDocument();

        try
        {
            await using var stream = new FileStream(
                backupPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            StoreDocument? document =
                await JsonSerializer.DeserializeAsync<StoreDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

            return document is not null &&
                   document.SchemaVersion == CurrentSchemaVersion
                ? document
                : new StoreDocument();
        }
        catch (JsonException)
        {
            return new StoreDocument();
        }
        catch (IOException)
        {
            return new StoreDocument();
        }
    }

    private async Task WriteDocumentUnsafeAsync(
        StoreDocument document,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_storePath);

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("مسیر ذخیره‌سازی وضعیت انتقال معتبر نیست.");

        Directory.CreateDirectory(directory);

        string tempPath = _storePath + ".tmp";
        string backupPath = _storePath + ".bak";

        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                document,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_storePath))
        {
            try
            {
                File.Replace(
                    tempPath,
                    _storePath,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(_storePath, backupPath, overwrite: true);
                File.Move(tempPath, _storePath, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(_storePath, backupPath, overwrite: true);
                File.Move(tempPath, _storePath, overwrite: true);
            }
        }
        else
        {
            File.Move(tempPath, _storePath);
        }
    }

    private static bool IsValidRecord(TransferResumeRecord record)
    {
        try
        {
            record.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
