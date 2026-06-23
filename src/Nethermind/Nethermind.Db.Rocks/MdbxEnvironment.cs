// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxEnvironment : IDisposable
{
    private readonly object _writeLock = new();
    private readonly object _batchGroupLock = new();
    private readonly Queue<MdbxBatchGroupRequest> _batchGroupQueue = new();
    private readonly ILogger _logger;
    private readonly MdbxProfiler? _profiler;
    private readonly MdbxValueCompression _valueCompression;
    private readonly bool _batchGroupingEnabled;
    private readonly bool _appendEnabled;
    private readonly int _maxBatchGroupOperations;
    private readonly long _maxBatchGroupBytes;
    private uint? _statsDbi;
    private bool _batchGroupActive;
    private bool _disposed;

    public MdbxEnvironment(string path, IDbConfig dbConfig, IRocksDbConfig rocksDbConfig, ILogger logger)
    {
        MdbxNative.EnsureSupported();

        Path = path;
        _logger = logger;

        ThrowIfRocksDbStoreExists(Path);
        Directory.CreateDirectory(Path);
        _valueCompression = MdbxValueCompression.Create(rocksDbConfig, logger, Path);

        MdbxTuningOptions tuning = MdbxTuningOptions.ReadFromEnvironment(logger, Path);
        _batchGroupingEnabled = tuning.EnableBatchGrouping;
        _appendEnabled = tuning.EnableAppend;
        _maxBatchGroupOperations = tuning.MaxBatchGroupOperations;
        _maxBatchGroupBytes = tuning.MaxBatchGroupBytes;
        if (tuning.HasOverrides && logger.IsInfo)
        {
            logger.Info($"MDBX tuning for {Path}: {tuning.Describe()}");
        }

        MdbxNative.ThrowOnError(MdbxNative.EnvCreate(out MdbxNative.SafeMdbxEnvHandle env), "mdbx_env_create");
        Env = env;
        MdbxNative.SetMaxDbs(Env, tuning.MaxDbs);
        MdbxNative.SetMaxReaders(Env, tuning.MaxReaders);
        MdbxNative.SetRpAugmentLimit(Env, tuning.RpAugmentLimit);
        if (tuning.DirtyPagesReserveLimit != 0)
        {
            MdbxNative.SetDirtyPagesReserveLimit(Env, tuning.DirtyPagesReserveLimit);
        }

        if (tuning.TransactionDirtyPagesLimit != 0)
        {
            MdbxNative.SetTransactionDirtyPagesLimit(Env, tuning.TransactionDirtyPagesLimit);
        }

        if (tuning.TransactionDirtyPagesInitial != 0)
        {
            MdbxNative.SetTransactionDirtyPagesInitial(Env, tuning.TransactionDirtyPagesInitial);
        }

        int pageSize = IsNewMdbxEnvironment(Path) ? tuning.PageSize : -1;
        MdbxNative.ThrowOnError(
            MdbxNative.EnvSetGeometry(Env, 0, (nint)tuning.InitialMapSize, unchecked((nint)tuning.MaxMapSize), (nint)tuning.GrowthStep, unchecked((nint)tuning.ShrinkThreshold), pageSize),
            "mdbx_env_set_geometry");

        uint flags = MdbxNative.EnvNoStickyThreads | MdbxNative.EnvLifoReclaim | MdbxNative.EnvNoMemInit;
        if (tuning.EnableWriteMap)
        {
            flags |= MdbxNative.EnvWriteMap;
        }

        if (tuning.EnableCoalesce)
        {
            flags |= MdbxNative.EnvCoalesce;
        }

        if (!tuning.EnableReadAhead)
        {
            flags |= MdbxNative.EnvNoReadAhead;
        }

        if (!dbConfig.WriteAheadLogSync || !rocksDbConfig.WriteAheadLogSync)
        {
            flags |= MdbxNative.EnvNoMetaSync;
        }

        // MDBX consumes the path synchronously during env_open; the UTF-8 buffer is pinned for the call.
        unsafe
        {
            byte[] pathBytes = MdbxNative.ToUtf8Z(Path);
            fixed (byte* pathPointer = pathBytes)
            {
                MdbxNative.ThrowOnError(MdbxNative.EnvOpen(Env, pathPointer, flags, Convert.ToUInt32("640", 8)), "mdbx_env_open");
            }
        }

        _profiler = MdbxProfiler.Create(Path, tuning, logger, TryReadStorageStats);
    }

    public string Path { get; }

    public MdbxNative.SafeMdbxEnvHandle Env { get; }

    public uint OpenTable(string? name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_writeLock)
        {
            using MdbxNative.SafeMdbxTxnHandle txn = BeginWriteTransaction();
            uint dbi;
            // MDBX copies or resolves the database name before dbi_open returns; the name buffer is pinned for the call.
            unsafe
            {
                byte[]? nameBytes = name is null ? null : MdbxNative.ToUtf8Z(name);
                fixed (byte* namePointer = nameBytes)
                {
                    MdbxNative.ThrowOnError(MdbxNative.DbiOpen(txn, namePointer, MdbxNative.Create, out dbi), "mdbx_dbi_open");
                }
            }

            MdbxNative.Commit(txn);
            _statsDbi ??= dbi;
            return dbi;
        }
    }

    public MdbxNative.SafeMdbxTxnHandle BeginReadOnlyTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MdbxNative.ThrowOnError(MdbxNative.TxnBegin(Env, IntPtr.Zero, MdbxNative.ReadOnly, out MdbxNative.SafeMdbxTxnHandle txn), "mdbx_txn_begin(readonly)");
        return txn;
    }

    public void ExecuteRead(Action<MdbxNative.SafeMdbxTxnHandle> action)
    {
        long started = _profiler?.StartReadTransaction() ?? 0;
        try
        {
            using MdbxNative.SafeMdbxTxnHandle txn = BeginReadOnlyTransaction();
            action(txn);
        }
        finally
        {
            if (_profiler is not null)
            {
                _profiler.RecordReadTransaction(started);
            }
        }
    }

    public TResult ExecuteRead<TResult>(Func<MdbxNative.SafeMdbxTxnHandle, TResult> action)
    {
        long started = _profiler?.StartReadTransaction() ?? 0;
        try
        {
            using MdbxNative.SafeMdbxTxnHandle txn = BeginReadOnlyTransaction();
            return action(txn);
        }
        finally
        {
            if (_profiler is not null)
            {
                _profiler.RecordReadTransaction(started);
            }
        }
    }

    public void ExecuteWrite(Action<MdbxNative.SafeMdbxTxnHandle> action) =>
        ExecuteWrite(action, operationCount: 1);

    public void ExecuteWrite(Action<MdbxNative.SafeMdbxTxnHandle> action, int operationCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_profiler is null)
        {
            lock (_writeLock)
            {
                using MdbxNative.SafeMdbxTxnHandle txn = BeginWriteTransaction();
                action(txn);
                MdbxNative.Commit(txn);
            }

            return;
        }

        bool lockTaken = false;
        long waitStarted = Stopwatch.GetTimestamp();
        long lockAcquired = 0;
        try
        {
            Monitor.Enter(_writeLock, ref lockTaken);
            lockAcquired = Stopwatch.GetTimestamp();
            using MdbxNative.SafeMdbxTxnHandle txn = BeginWriteTransaction();
            action(txn);
            MdbxNative.Commit(txn);
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_writeLock);
                _profiler.RecordWriteTransaction(lockAcquired - waitStarted, Stopwatch.GetTimestamp() - lockAcquired, operationCount);
            }
        }
    }

    public void ApplyBatch(IReadOnlyList<MdbxWriteOperation> operations)
    {
        if (operations.Count == 0)
        {
            return;
        }

        if (!_batchGroupingEnabled)
        {
            ExecuteBatch(operations);
            return;
        }

        ApplyBatchGroup(operations);
    }

    private void ExecuteBatch(IReadOnlyList<MdbxWriteOperation> operations) =>
        ExecuteWrite(txn =>
        {
            MdbxAppendTracker? appendTracker = _appendEnabled ? new MdbxAppendTracker(this, txn) : null;
            for (int i = 0; i < operations.Count; i++)
            {
                ApplyOperation(txn, operations[i], appendTracker);
            }
        }, operations.Count);

    private void ApplyBatchGroup(IReadOnlyList<MdbxWriteOperation> operations)
    {
        MdbxBatchGroupRequest request = new(operations);
        bool shouldRunBatchGroup;

        lock (_batchGroupLock)
        {
            _batchGroupQueue.Enqueue(request);
            shouldRunBatchGroup = !_batchGroupActive;
            if (shouldRunBatchGroup)
            {
                _batchGroupActive = true;
            }
        }

        if (shouldRunBatchGroup)
        {
            DrainBatchGroups();
        }
        else
        {
            WaitForBatchGroupRequest(request);
        }

        request.ThrowIfFailed();
    }

    private void DrainBatchGroups()
    {
        List<MdbxBatchGroupRequest> group = [];
        while (true)
        {
            int operationCount = DequeueBatchGroup(group, out long byteCount);
            if (operationCount == 0)
            {
                return;
            }

            Exception? exception = null;
            try
            {
                ExecuteWrite(txn =>
                {
                    MdbxAppendTracker? appendTracker = _appendEnabled ? new MdbxAppendTracker(this, txn) : null;
                    for (int requestIndex = 0; requestIndex < group.Count; requestIndex++)
                    {
                        IReadOnlyList<MdbxWriteOperation> operations = group[requestIndex].Operations;
                        for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
                        {
                            ApplyOperation(txn, operations[operationIndex], appendTracker);
                        }
                    }
                }, operationCount);
                _profiler?.RecordBatchGroup(group.Count, operationCount, byteCount);
            }
            catch (Exception caught)
            {
                exception = caught;
            }

            CompleteBatchGroup(group, exception);
            group.Clear();
        }
    }

    private int DequeueBatchGroup(List<MdbxBatchGroupRequest> group, out long byteCount)
    {
        int operationCount = 0;
        byteCount = 0;
        lock (_batchGroupLock)
        {
            while (_batchGroupQueue.Count > 0)
            {
                MdbxBatchGroupRequest request = _batchGroupQueue.Peek();
                if (group.Count > 0 &&
                    (operationCount + request.OperationCount > _maxBatchGroupOperations ||
                    byteCount + request.ByteCount > _maxBatchGroupBytes))
                {
                    break;
                }

                _batchGroupQueue.Dequeue();
                group.Add(request);
                operationCount += request.OperationCount;
                byteCount += request.ByteCount;
            }

            if (group.Count == 0)
            {
                _batchGroupActive = false;
                Monitor.PulseAll(_batchGroupLock);
            }
        }

        return operationCount;
    }

    private void CompleteBatchGroup(List<MdbxBatchGroupRequest> group, Exception? exception)
    {
        lock (_batchGroupLock)
        {
            for (int i = 0; i < group.Count; i++)
            {
                group[i].Complete(exception);
            }

            Monitor.PulseAll(_batchGroupLock);
        }
    }

    private void WaitForBatchGroupRequest(MdbxBatchGroupRequest request)
    {
        lock (_batchGroupLock)
        {
            while (!request.IsCompleted)
            {
                Monitor.Wait(_batchGroupLock);
            }
        }
    }

    public void DropTable(uint dbi) =>
        ExecuteWrite(txn => MdbxNative.ThrowOnError(MdbxNative.Drop(txn, dbi, delete: false), "mdbx_drop"));

    public void DropTables(uint[] dbis, int count) =>
        ExecuteWrite(txn =>
        {
            for (int i = 0; i < count; i++)
            {
                MdbxNative.ThrowOnError(MdbxNative.Drop(txn, dbis[i], delete: false), "mdbx_drop");
            }
        }, count);

    public void Flush() =>
        MdbxNative.ThrowOnError(MdbxNative.EnvSyncEx(Env, force: true, nonblock: false), "mdbx_env_sync_ex");

    public void ApplyOperation(MdbxNative.SafeMdbxTxnHandle txn, in MdbxWriteOperation operation) =>
        ApplyOperation(txn, operation, appendTracker: null);

    private void ApplyOperation(MdbxNative.SafeMdbxTxnHandle txn, in MdbxWriteOperation operation, MdbxAppendTracker? appendTracker)
    {
        switch (operation.Kind)
        {
            case MdbxWriteKind.Set:
                bool tryAppend = appendTracker?.TryAppend(operation) == true;
                MdbxAppendResult appendResult = Put(txn, operation.Dbi, operation.Key, operation.Value, tryAppend);
                appendTracker?.RecordResult(operation, appendResult);
                break;
            case MdbxWriteKind.Delete:
                Delete(txn, operation.Dbi, operation.Key);
                break;
            case MdbxWriteKind.Merge:
                Merge(txn, operation.Dbi, operation.Key, operation.Value ?? [], operation.MergeOperator);
                break;
            default:
                throw new InvalidOperationException($"Unknown MDBX write operation {operation.Kind}.");
        }
    }

    public byte[]? Get(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key)
    {
        // mdbx_get only observes the key during the call and returns data owned by the read transaction.
        unsafe
        {
            fixed (byte* keyPointer = key)
            {
                MdbxValue keyValue = new() { Base = (IntPtr)keyPointer, Length = (nuint)key.Length };
                int result = MdbxNative.Get(txn, dbi, ref keyValue, out MdbxValue dataValue);
                if (result == MdbxNative.NotFound)
                {
                    _profiler?.RecordGet(hit: false, key.Length, valueBytes: 0);
                    return null;
                }

                MdbxNative.ThrowOnError(result, "mdbx_get");
                byte[] data = CopyValue(dataValue);
                _profiler?.RecordGet(hit: true, key.Length, data.Length);
                return data;
            }
        }
    }

    public byte[]? Get(uint dbi, ReadOnlySpan<byte> key)
    {
        byte[] keyCopy = key.ToArray();
        return ExecuteRead(txn => Get(txn, dbi, keyCopy));
    }

    public bool TryGet(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key, out byte[]? value)
    {
        value = Get(txn, dbi, key);
        return value is not null;
    }

    public void Put(uint dbi, ReadOnlySpan<byte> key, byte[]? value)
    {
        byte[] keyCopy = key.ToArray();
        ExecuteWrite(txn => Put(txn, dbi, keyCopy, value));
    }

    public void Put(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key, byte[]? value)
    {
        if (value is null)
        {
            Delete(txn, dbi, key);
            return;
        }

        Put(txn, dbi, key, value.AsSpan());
    }

    public void Put(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) =>
        Put(txn, dbi, key, value, tryAppend: false);

    private unsafe MdbxAppendResult Put(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool tryAppend)
    {
        byte[]? storedBuffer = _valueCompression.TryEncode(value, out byte[]? encoded, out int storedLength, out MdbxValueEncodingKind encodingKind)
            ? encoded
            : null;
        ReadOnlySpan<byte> storedValue = storedBuffer is null ? value : storedBuffer.AsSpan(0, storedLength);

        // MDBX copies key/data into the map before mdbx_put returns; both spans are pinned for that call.
        fixed (byte* keyPointer = key)
        fixed (byte* valuePointer = storedValue)
        {
            MdbxValue keyValue = new() { Base = (IntPtr)keyPointer, Length = (nuint)key.Length };
            MdbxValue dataValue = new() { Base = (IntPtr)valuePointer, Length = (nuint)storedValue.Length };
            int result = MdbxNative.Put(txn, dbi, ref keyValue, ref dataValue, tryAppend ? MdbxNative.PutAppend : MdbxNative.PutUpsert);
            if (tryAppend && result is MdbxNative.KeyExists or MdbxNative.KeyMismatch)
            {
                _profiler?.RecordAppend(success: false, fallback: true);
                result = MdbxNative.Put(txn, dbi, ref keyValue, ref dataValue, MdbxNative.PutUpsert);
                MdbxNative.ThrowOnError(result, "mdbx_put");
                _profiler?.RecordPut(key.Length, value.Length);
                _profiler?.RecordValueEncoding(encodingKind, value.Length, storedValue.Length);
                return MdbxAppendResult.Fallback;
            }
            else if (tryAppend)
            {
                _profiler?.RecordAppend(success: result == MdbxNative.Success, fallback: false);
            }

            MdbxNative.ThrowOnError(result, "mdbx_put");
            _profiler?.RecordPut(key.Length, value.Length);
            _profiler?.RecordValueEncoding(encodingKind, value.Length, storedValue.Length);
            return tryAppend ? MdbxAppendResult.Succeeded : MdbxAppendResult.NotAttempted;
        }
    }

    public void Delete(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key)
    {
        // MDBX only reads the key while deleting; the span stays pinned until mdbx_del returns.
        unsafe
        {
            fixed (byte* keyPointer = key)
            {
                MdbxValue keyValue = new() { Base = (IntPtr)keyPointer, Length = (nuint)key.Length };
                int result = MdbxNative.Del(txn, dbi, ref keyValue, IntPtr.Zero);
                if (result is not MdbxNative.Success and not MdbxNative.NotFound)
                {
                    MdbxNative.ThrowOnError(result, "mdbx_del");
                }

                _profiler?.RecordDelete();
            }
        }
    }

    public void Merge(uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IMergeOperator? mergeOperator)
    {
        byte[] keyCopy = key.ToArray();
        byte[] valueCopy = value.ToArray();
        ExecuteWrite(txn => Merge(txn, dbi, keyCopy, valueCopy, mergeOperator));
    }

    public void Merge(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IMergeOperator? mergeOperator)
    {
        if (mergeOperator is null)
        {
            throw new InvalidOperationException("MDBX merge was requested for a database without a merge operator.");
        }

        byte[] keyCopy = key.ToArray();
        byte[]? existing = Get(txn, dbi, key);
        byte[] valueCopy = value.ToArray();
        _profiler?.RecordMerge();

        // Merge operands are copied or consumed before FullMerge returns; pinned arrays do not escape this block.
        unsafe
        {
            Span<IntPtr> operands = stackalloc IntPtr[1];
            Span<long> operandLengths = stackalloc long[1];
            operandLengths[0] = valueCopy.Length;

            fixed (byte* valuePointer = valueCopy)
            fixed (byte* existingPointer = existing)
            {
                operands[0] = (IntPtr)valuePointer;
                Span<byte> existingSpan = existing is null ? default : new Span<byte>(existingPointer, existing.Length);
                RocksDbMergeEnumerator enumerator = new(existingSpan, existing is not null, operands, operandLengths);

                using ArrayPoolList<byte> merged = mergeOperator.FullMerge(keyCopy, enumerator)
                    ?? throw new InvalidOperationException($"MDBX merge operator {mergeOperator.GetType().Name} rejected the merge.");

                Put(txn, dbi, keyCopy, merged.AsSpan());
            }
        }
    }

    public static byte[] Copy(MdbxValue value)
    {
        if (value.Base == IntPtr.Zero && value.Length == 0)
        {
            return [];
        }

        byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)value.Length));
        // MDBX values point into transaction-owned memory; copy immediately into managed storage.
        unsafe
        {
            new ReadOnlySpan<byte>((void*)value.Base, data.Length).CopyTo(data);
        }

        return data;
    }

    public byte[] CopyValue(MdbxValue value)
    {
        if (value.Base == IntPtr.Zero && value.Length == 0)
        {
            return [];
        }

        unsafe
        {
            return _valueCompression.Decode(new ReadOnlySpan<byte>((void*)value.Base, checked((int)value.Length)));
        }
    }

    public long GetDirectorySize()
    {
        if (!Directory.Exists(Path))
        {
            return 0;
        }

        long size = 0;
        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            FileInfo fileInfo = new(file);
            size += fileInfo.Length;
        }

        return size;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            try
            {
                Flush();
            }
            catch (Exception exception) when (exception is MdbxException or DllNotFoundException or EntryPointNotFoundException)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to flush MDBX environment {Path}: {exception.Message}");
            }

            _profiler?.ReportFinal();
        }
        finally
        {
            _disposed = true;
            _valueCompression.Dispose();
            Env.Dispose();
        }
    }

    public void RecordQueuedWrite(ReadOnlySpan<byte> key, byte[]? value, int pendingOperations) =>
        _profiler?.RecordQueuedWrite(key.Length, value?.Length ?? 0, pendingOperations);

    public void RecordQueuedWrite(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, int pendingOperations) =>
        _profiler?.RecordQueuedWrite(key.Length, value.Length, pendingOperations);

    private MdbxNative.SafeMdbxTxnHandle BeginWriteTransaction()
    {
        MdbxNative.ThrowOnError(MdbxNative.TxnBegin(Env, IntPtr.Zero, 0, out MdbxNative.SafeMdbxTxnHandle txn), "mdbx_txn_begin(write)");
        return txn;
    }

    private static void ThrowIfRocksDbStoreExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        bool hasRocksManifest = Directory.EnumerateFiles(path, "MANIFEST-*").Any();
        bool hasRocksCurrent = File.Exists(System.IO.Path.Combine(path, "CURRENT"));
        bool hasRocksTables = Directory.EnumerateFiles(path, "*.sst").Any();
        if (hasRocksCurrent || hasRocksManifest || hasRocksTables)
        {
            throw new InvalidOperationException(
                $"Existing RocksDB data was found in '{path}'. The MDBX backend does not migrate RocksDB data; delete this database directory and sync from scratch.");
        }
    }

    private MdbxStorageStats? TryReadStorageStats()
    {
        if (_disposed || _statsDbi is not uint dbi)
        {
            return null;
        }

        using MdbxNative.SafeMdbxTxnHandle txn = BeginReadOnlyTransaction();
        MdbxNative.ThrowOnError(
            MdbxNative.DbiStat(txn, dbi, out MdbxNative.MdbxStat stat, (nuint)Marshal.SizeOf<MdbxNative.MdbxStat>()),
            "mdbx_dbi_stat");

        return new MdbxStorageStats(
            stat.PageSize,
            stat.Depth,
            stat.BranchPages,
            stat.LeafPages,
            stat.OverflowPages,
            stat.Entries,
            stat.ModTxnId);
    }

    private static bool IsNewMdbxEnvironment(string path) =>
        !File.Exists(System.IO.Path.Combine(path, "mdbx.dat"));

    internal byte[]? TryGetLastKey(MdbxNative.SafeMdbxTxnHandle txn, uint dbi)
    {
        using MdbxNative.SafeMdbxCursorHandle cursor = MdbxCursorHelpers.OpenCursor(txn, dbi);
        MdbxValue key = default;
        MdbxValue data = default;
        int result = MdbxNative.CursorGet(cursor, ref key, ref data, MdbxCursorOp.Last);
        if (result == MdbxNative.NotFound)
        {
            return null;
        }

        MdbxNative.ThrowOnError(result, "mdbx_cursor_get(last)");
        return Copy(key);
    }
}

internal enum MdbxWriteKind
{
    Set,
    Delete,
    Merge,
}

internal enum MdbxAppendResult
{
    NotAttempted,
    Succeeded,
    Fallback,
}

internal readonly record struct MdbxWriteOperation(MdbxWriteKind Kind, uint Dbi, byte[] Key, byte[]? Value, IMergeOperator? MergeOperator)
{
    public int ApproximateByteLength => Key.Length + (Value?.Length ?? 0);

    public static MdbxWriteOperation Set(uint dbi, ReadOnlySpan<byte> key, byte[]? value, IMergeOperator? mergeOperator) =>
        value is null
            ? new MdbxWriteOperation(MdbxWriteKind.Delete, dbi, key.ToArray(), null, mergeOperator)
            : new MdbxWriteOperation(MdbxWriteKind.Set, dbi, key.ToArray(), value.AsSpan().ToArray(), mergeOperator);

    public static MdbxWriteOperation PutSpan(uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IMergeOperator? mergeOperator) =>
        new(MdbxWriteKind.Set, dbi, key.ToArray(), value.ToArray(), mergeOperator);

    public static MdbxWriteOperation Merge(uint dbi, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IMergeOperator? mergeOperator) =>
        new(MdbxWriteKind.Merge, dbi, key.ToArray(), value.ToArray(), mergeOperator);
}

internal sealed class MdbxBatchGroupRequest(IReadOnlyList<MdbxWriteOperation> operations)
{
    private Exception? _exception;

    public IReadOnlyList<MdbxWriteOperation> Operations { get; } = operations;

    public int OperationCount => Operations.Count;

    public long ByteCount { get; } = CalculateByteCount(operations);

    public bool IsCompleted { get; private set; }

    public void Complete(Exception? exception)
    {
        _exception = exception;
        IsCompleted = true;
    }

    public void ThrowIfFailed()
    {
        if (_exception is not null)
        {
            ExceptionDispatchInfo.Capture(_exception).Throw();
        }
    }

    private static long CalculateByteCount(IReadOnlyList<MdbxWriteOperation> operations)
    {
        long byteCount = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            byteCount += operations[i].ApproximateByteLength;
        }

        return byteCount;
    }
}

internal sealed class MdbxAppendTracker(MdbxEnvironment environment, MdbxNative.SafeMdbxTxnHandle txn)
{
    private readonly Dictionary<uint, byte[]?> _lastKeys = [];
    private HashSet<uint>? _disabledDbis;

    public bool TryAppend(in MdbxWriteOperation operation)
    {
        if (operation.Kind != MdbxWriteKind.Set ||
            operation.Value is null ||
            operation.MergeOperator is not null ||
            (_disabledDbis is not null && _disabledDbis.Contains(operation.Dbi)))
        {
            return false;
        }

        if (!_lastKeys.TryGetValue(operation.Dbi, out byte[]? lastKey))
        {
            lastKey = environment.TryGetLastKey(txn, operation.Dbi);
            _lastKeys.Add(operation.Dbi, lastKey);
        }

        if (lastKey is not null && operation.Key.AsSpan().SequenceCompareTo(lastKey) <= 0)
        {
            return false;
        }

        return true;
    }

    public void RecordResult(in MdbxWriteOperation operation, MdbxAppendResult result)
    {
        if (result == MdbxAppendResult.Succeeded)
        {
            _lastKeys[operation.Dbi] = operation.Key;
        }
        else if (result == MdbxAppendResult.Fallback)
        {
            (_disabledDbis ??= []).Add(operation.Dbi);
        }
    }
}
