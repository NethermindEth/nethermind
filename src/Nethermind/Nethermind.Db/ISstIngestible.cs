// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Db;

/// <summary>
/// A column store that can persist a batch of writes by building a sorted SST file off-heap and
/// ingesting it as a metadata-only add, bypassing the memtable → flush → L0-compaction burst that a
/// single large WriteBatch triggers. The returned batch buffers point puts/deletes (last write wins);
/// on <see cref="System.IDisposable.Dispose"/> it sorts them, writes one SST, and ingests it into this column.
/// </summary>
public interface ISstIngestible
{
    IWriteBatch StartSstIngestBatch();
}
