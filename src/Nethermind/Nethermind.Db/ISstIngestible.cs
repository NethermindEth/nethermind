// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Db;

/// <summary>Column store that persists a write batch by ingesting a sorted SST file.</summary>
public interface ISstIngestible
{
    IWriteBatch StartSstIngestBatch();
}
