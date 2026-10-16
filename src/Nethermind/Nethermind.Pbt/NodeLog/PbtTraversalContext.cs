// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Pbt.NodeLog;

/// <summary>The log, root hint and unpublished group references of one canonical root update.</summary>
/// <remarks>The caller owns the operation lease and keeps it alive through worker completion and snapshot publication.</remarks>
/// <param name="countReads">Collect diagnostic read counts; disabled by default to avoid contended counters on production traversal.</param>
public sealed class PbtTraversalContext(PbtGroupLog log, PbtGroupLog.OperationLease operation, PbtGroupReference rootReference, bool countReads = false) : IDisposable
{
    private readonly ArrayPoolList<ArrayPoolList<WrittenGroup>> _writes = new(16);
    private long _directReads;
    private long _fallbackReads;

    /// <summary>The shared group log.</summary>
    public PbtGroupLog Log { get; } = log;

    /// <summary>The borrowed lease protecting this update and its publication.</summary>
    public PbtGroupLog.OperationLease Operation { get; } = operation;

    /// <summary>The input root hint, replaced by the completed output root after a successful update.</summary>
    public PbtGroupReference RootReference { get; private set; } = rootReference;

    /// <summary>Whether the entire root update finished successfully.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Direct record reads when diagnostic counting is enabled, otherwise zero.</summary>
    public long DirectReads => Interlocked.Read(ref _directReads);

    /// <summary>Exact-snapshot fallback reads when diagnostic counting is enabled, otherwise zero.</summary>
    public long FallbackReads => Interlocked.Read(ref _fallbackReads);

    internal bool TryRead(PbtGroupReference reference, scoped in PbtTraversalPath path, out PbtGroupRecord record)
    {
        if (Log.TryGetRecord(Operation, reference, out record) && PbtNodePathOperations.Equal(record.Path, path.ToPath<PbtStorageNodePath>()))
        {
            if (countReads) Interlocked.Increment(ref _directReads);
            return true;
        }
        if (countReads) Interlocked.Increment(ref _fallbackReads);
        return false;
    }

    internal Writer CreateWriter() => new(this);

    internal void Complete(PbtGroupReference reference)
    {
        RootReference = reference;
        IsComplete = true;
    }

    /// <summary>Applies a successful update's handed-off references at the existing owner publication boundary.</summary>
    public void Apply(Action<PbtStorageNodePath, PbtGroupReference> publish)
    {
        if (!IsComplete) return;
        foreach (ArrayPoolList<WrittenGroup> buffer in _writes)
            foreach (WrittenGroup group in buffer) publish(group.Path, group.Reference);
    }

    /// <summary>Returns unpublished write buffers; the caller still owns the operation lease.</summary>
    public void Dispose()
    {
        foreach (ArrayPoolList<WrittenGroup> buffer in _writes) buffer.Dispose();
        _writes.Dispose();
    }

    private readonly record struct WrittenGroup(PbtStorageNodePath Path, PbtGroupReference Reference);

    internal sealed class Writer(PbtTraversalContext owner) : IDisposable
    {
        private readonly ArrayPoolList<WrittenGroup> _buffer = new(16);

        public void SetNodeGroup(scoped in PbtTraversalPath path, PbtGroupReference reference) =>
            _buffer.Add(new WrittenGroup(path.ToPath<PbtStorageNodePath>(), reference));

        public void Dispose()
        {
            if (_buffer.Count == 0) _buffer.Dispose();
            else lock (owner._writes) owner._writes.Add(_buffer);
        }
    }
}
