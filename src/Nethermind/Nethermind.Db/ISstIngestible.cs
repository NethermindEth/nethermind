// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Db;

/// <summary>Column store that persists a write batch by staging sorted SST files and ingesting them at commit.</summary>
public interface ISstIngestible
{
    ISstIngestWriteBatch StartSstIngestBatch();

    /// <summary>Ingests previously staged SST files into the column family in a single native call.</summary>
    void IngestStagedFiles(IReadOnlyList<string> files);

    /// <summary>Bounded wait until the column's L0 drains below the ingest throttle threshold, or the token trips.</summary>
    void WaitForIngestCompactionHeadroom(CancellationToken cancellationToken);

    string IngestStagingDir { get; }
}

/// <summary>
/// Write batch that buffers into staged SST files; nothing becomes visible until <see cref="IngestStagedFiles"/>.
/// Dispose releases buffers only — staged files that were neither ingested nor deleted stay on disk.
/// </summary>
public interface ISstIngestWriteBatch : IWriteBatch
{
    /// <summary>Flushes remaining buffered writes to staged SST files and returns every staged file path.</summary>
    IReadOnlyList<string> SealToStagedFiles();

    /// <summary>Ingests all staged files into the column family.</summary>
    void IngestStagedFiles();

    /// <summary>Deletes staged files that were never ingested (failure cleanup).</summary>
    void DeleteStagedFiles();
}
