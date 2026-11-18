// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence;

internal sealed class AtomicPersistenceBatch(
    IAtomicColumnsWriteBatch<FlatDbColumns> batch,
    IColumnDbSnapshot<FlatDbColumns> snapshot,
    StateId from,
    StateId to,
    FlatLayout layout,
    bool recordLayout,
    Action recordLayoutCommitted,
    IDbMeta db,
    WriteFlags flags,
    WriteBufferAdjuster adjuster,
    ILogger logger) : IDisposable
{
    private bool _disposed;

    internal void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal void Commit()
    {
        EnsureOpen();
        try
        {
            if (from != StateId.Sync && to != StateId.Sync)
                BasePersistence.SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to, flags);
            if (recordLayout)
                BasePersistence.SetLayout(batch.GetColumnBatch(FlatDbColumns.Metadata), layout, flags);
            batch.Commit();
            if (!flags.HasFlag(WriteFlags.DisableWAL)) db.SyncWal();
            if (recordLayout) recordLayoutCommitted();
        }
        finally
        {
            try
            {
                Dispose();
            }
            catch (Exception exception)
            {
                if (logger.IsError) logger.Error("Failed to release the persistence transaction.", exception);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            batch.Dispose();
        }
        finally
        {
            try
            {
                snapshot.Dispose();
            }
            finally
            {
                adjuster.OnBatchDisposed();
            }
        }
    }
}
