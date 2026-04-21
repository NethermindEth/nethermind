// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Decorator that records the configured <see cref="FlatLayout"/> into the flat DB's metadata column
/// on the first write batch, so a subsequent start with a different layout can be refused. Startup-time
/// mismatch detection lives in <see cref="BasePersistence.EnsureLayout"/>.
/// </summary>
public class LayoutRecordingPersistence(IPersistence inner, IColumnsDb<FlatDbColumns> db, FlatLayout layout) : IPersistence
{
    private int _layoutPersisted = BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)) is null ? 0 : 1;

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IPersistence.IWriteBatch batch = inner.CreateWriteBatch(from, to, flags);
        if (Interlocked.CompareExchange(ref _layoutPersisted, 1, 0) == 0)
        {
            BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), layout);
        }
        return batch;
    }

    public void Flush() => inner.Flush();

    public void Clear() => inner.Clear();
}
