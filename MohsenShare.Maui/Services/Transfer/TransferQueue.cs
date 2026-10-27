
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MohsenShare.Maui.Services.Transfer;

public sealed class TransferQueue
{
    private readonly object _sync = new();
    private readonly List<TransferResumeRecord> _items = [];

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    public IReadOnlyList<TransferResumeRecord> GetSnapshot()
    {
        lock (_sync)
        {
            return _items.ToArray();
        }
    }

    public void Load(IEnumerable<TransferResumeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var validated = records.ToList();

        foreach (TransferResumeRecord record in validated)
        {
            record.Validate();
        }

        lock (_sync)
        {
            _items.Clear();

            foreach (TransferResumeRecord record in validated)
            {
                if (_items.Any(x =>
                    string.Equals(
                        x.FileId,
                        record.FileId,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                _items.Add(record);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryAdd(TransferResumeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();

        lock (_sync)
        {
            if (_items.Any(x =>
                string.Equals(
                    x.FileId,
                    record.FileId,
                    StringComparison.Ordinal)))
            {
                return false;
            }

            _items.Add(record);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryGet(
        string fileId,
        out TransferResumeRecord? record)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            record = null;
            return false;
        }

        lock (_sync)
        {
            record = _items.FirstOrDefault(x =>
                string.Equals(
                    x.FileId,
                    fileId,
                    StringComparison.Ordinal));

            return record is not null;
        }
    }

    public bool TryUpdate(TransferResumeRecord updatedRecord)
    {
        ArgumentNullException.ThrowIfNull(updatedRecord);
        updatedRecord.Validate();

        lock (_sync)
        {
            int index = _items.FindIndex(x =>
                string.Equals(
                    x.FileId,
                    updatedRecord.FileId,
                    StringComparison.Ordinal));

            if (index < 0)
            {
                return false;
            }

            _items[index] = updatedRecord;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRemove(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return false;
        }

        bool removed;

        lock (_sync)
        {
            int index = _items.FindIndex(x =>
                string.Equals(
                    x.FileId,
                    fileId,
                    StringComparison.Ordinal));

            removed = index >= 0;

            if (removed)
            {
                _items.RemoveAt(index);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public TransferResumeRecord? GetNextQueued()
    {
        lock (_sync)
        {
            return _items.FirstOrDefault(x =>
                x.State == TransferFileState.Queued);
        }
    }

    public IReadOnlyList<TransferResumeRecord> GetResumable()
    {
        lock (_sync)
        {
            return _items
                .Where(x => x.IsResumable)
                .ToArray();
        }
    }
}
