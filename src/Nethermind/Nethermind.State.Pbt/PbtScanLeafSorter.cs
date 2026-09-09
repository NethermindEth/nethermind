// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Sorts scanner leaves using a fixed record buffer and bounded merge fan-in.</summary>
/// <remarks>The caller must dispose both the sorted enumerator and this owner, including after failure.</remarks>
internal sealed class PbtScanLeafSorter : IDisposable
{
    private const int BufferByteBudget = 16 * 1024 * 1024;
    private const int StreamBufferSize = 64 * 1024;
    private const int RecordSize = 1 + PbtStorageFullKey.MaxLength + ValueHash256.MemorySize;
    private static readonly IComparer<KeyValuePair<PbtStorageFullKey, ValueHash256>> RecordComparer =
        Comparer<KeyValuePair<PbtStorageFullKey, ValueHash256>>.Create(static (left, right) => left.Key.CompareTo(right.Key));

    private readonly KeyValuePair<PbtStorageFullKey, ValueHash256>[] _buffer;
    private readonly int _mergeFanIn;
    private readonly Action<string, long>? _progress;
    private readonly Action<Exception>? _cleanupFailure;
    private int _bufferCount;
    private long _runsInPass;
    private long _spilledRecords;
    private long _mergedRecords;
    private bool _sorting;
    private bool _disposed;

    internal PbtScanLeafSorter(string? temporaryParent = null, int bufferCapacity = 0, int mergeFanIn = 16, Action<string, long>? progress = null, Action<Exception>? cleanupFailure = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(mergeFanIn, 2);
        BufferCapacity = bufferCapacity == 0
            ? BufferByteBudget / Unsafe.SizeOf<KeyValuePair<PbtStorageFullKey, ValueHash256>>()
            : bufferCapacity;
        _buffer = new KeyValuePair<PbtStorageFullKey, ValueHash256>[BufferCapacity];
        _mergeFanIn = mergeFanIn;
        _progress = progress;
        _cleanupFailure = cleanupFailure;
        TemporaryDirectory = Path.Combine(temporaryParent ?? Path.GetTempPath(), $"nethermind-pbt-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TemporaryDirectory);
    }

    internal int BufferCapacity { get; }
    internal int PeakBufferedRecords { get; private set; }
    internal int PeakMergeReaders { get; private set; }
    internal long RunCount { get; private set; }
    internal string TemporaryDirectory { get; }

    internal void Add(PbtStorageFullKey key, ValueHash256 value, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sorting) throw new InvalidOperationException("Cannot add leaves after sorting has started.");
        cancellationToken.ThrowIfCancellationRequested();
        if (key.Length == 0) throw new ArgumentException("A complete leaf key is required.", nameof(key));
        _buffer[_bufferCount++] = new(key, value);
        PeakBufferedRecords = Math.Max(PeakBufferedRecords, _bufferCount);
        if (_bufferCount == BufferCapacity) Spill(cancellationToken);
    }

    internal IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> GetSorted(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sorting) throw new InvalidOperationException("Sorted leaves can only be enumerated once.");
        _sorting = true;
        cancellationToken.ThrowIfCancellationRequested();
        Spill(cancellationToken);
        int pass = 0;
        while (_runsInPass > _mergeFanIn)
        {
            string phase = $"merge pass {pass + 1}";
            _mergedRecords = 0;
            _progress?.Invoke(phase, 0);
            long outputRuns = 0;
            for (long firstRun = 0; firstRun < _runsInPass;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int readerCount = (int)Math.Min(_mergeFanIn, _runsInPass - firstRun);
                using (FileStream output = OpenWriter(pass + 1, outputRuns))
                {
                    foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> record in Merge(pass, firstRun, readerCount, phase, cancellationToken))
                        WriteRecord(output, record);
                }
                for (int readerIndex = 0; readerIndex < readerCount; readerIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(RunPath(pass, firstRun + readerIndex));
                }
                outputRuns++;
                firstRun += readerCount;
            }
            _runsInPass = outputRuns;
            pass++;
        }

        string finalPhase = $"merge pass {pass + 1}";
        _mergedRecords = 0;
        _progress?.Invoke(finalPhase, 0);
        foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> record in Merge(pass, 0, (int)_runsInPass, finalPhase, cancellationToken))
            yield return record;
    }

    private void Spill(CancellationToken cancellationToken)
    {
        if (_bufferCount == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        _progress?.Invoke("spill", _spilledRecords);
        Array.Sort(_buffer, 0, _bufferCount, RecordComparer);
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream output = OpenWriter(0, _runsInPass);
        for (int recordIndex = 0; recordIndex < _bufferCount; recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyValuePair<PbtStorageFullKey, ValueHash256> record = _buffer[recordIndex];
            if (recordIndex == 0 || !IsDuplicate(_buffer[recordIndex - 1], record))
                WriteRecord(output, record);
            _progress?.Invoke("spill", ++_spilledRecords);
        }
        _runsInPass++;
        _bufferCount = 0;
    }

    private IEnumerable<KeyValuePair<PbtStorageFullKey, ValueHash256>> Merge(
        int pass, long firstRun, int readerCount, string phase, CancellationToken cancellationToken)
    {
        RunReader?[] readers = new RunReader?[readerCount];
        PriorityQueue<int, PbtStorageFullKey> queue = new(readerCount);
        try
        {
            for (int readerIndex = 0; readerIndex < readerCount; readerIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunReader reader = new(RunPath(pass, firstRun + readerIndex));
                readers[readerIndex] = reader;
                PeakMergeReaders = Math.Max(PeakMergeReaders, readerIndex + 1);
                if (reader.MoveNext()) queue.Enqueue(readerIndex, reader.Current.Key);
            }

            KeyValuePair<PbtStorageFullKey, ValueHash256> previous = default;
            bool hasPrevious = false;
            while (queue.TryDequeue(out int readerIndex, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunReader reader = readers[readerIndex]!;
                KeyValuePair<PbtStorageFullKey, ValueHash256> record = reader.Current;
                bool duplicate = hasPrevious && IsDuplicate(previous, record);
                previous = record;
                hasPrevious = true;
                if (reader.MoveNext()) queue.Enqueue(readerIndex, reader.Current.Key);
                _progress?.Invoke(phase, ++_mergedRecords);
                if (!duplicate) yield return record;
            }
        }
        finally
        {
            foreach (RunReader? reader in readers) reader?.Dispose();
        }
    }

    private static bool IsDuplicate(KeyValuePair<PbtStorageFullKey, ValueHash256> previous, KeyValuePair<PbtStorageFullKey, ValueHash256> current)
    {
        if (previous.Key != current.Key) return false;
        if (previous.Value != current.Value) throw new InvalidDataException("Conflicting values for the same PBT leaf key.");
        return true;
    }

    private string RunPath(int pass, long run) => Path.Combine(TemporaryDirectory, $"{pass}-{run}.bin");

    private FileStream OpenWriter(int pass, long run)
    {
        FileStream output = new(RunPath(pass, run), FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, FileOptions.SequentialScan);
        RunCount++;
        return output;
    }

    private static void WriteRecord(FileStream output, KeyValuePair<PbtStorageFullKey, ValueHash256> record)
    {
        Span<byte> bytes = stackalloc byte[RecordSize];
        bytes.Clear();
        bytes[0] = (byte)record.Key.Length;
        record.Key.Bytes.CopyTo(bytes[1..]);
        record.Value.Bytes.CopyTo(bytes[(1 + PbtStorageFullKey.MaxLength)..]);
        output.Write(bytes);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Directory.Delete(TemporaryDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not replace the scan failure that caused disposal.
            if (_cleanupFailure is not null) _cleanupFailure(exception);
            else Trace.TraceWarning($"Unable to remove PBT scan temporary directory '{TemporaryDirectory}': {exception}");
        }
    }

    private sealed class RunReader(string path) : IDisposable
    {
        private readonly byte[] _record = new byte[RecordSize];
        private readonly FileStream _stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferSize, FileOptions.SequentialScan);

        internal KeyValuePair<PbtStorageFullKey, ValueHash256> Current { get; private set; }

        internal bool MoveNext()
        {
            if (_stream.Read(_record, 0, 1) == 0) return false;
            _stream.ReadExactly(_record.AsSpan(1));
            int keyLength = _record[0];
            if (keyLength is < 1 or > PbtStorageFullKey.MaxLength)
                throw new InvalidDataException("Invalid key length in PBT scan temporary run.");
            Current = new(new PbtStorageFullKey(_record.AsSpan(1, keyLength)), new ValueHash256(_record.AsSpan(1 + PbtStorageFullKey.MaxLength)));
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }
}
