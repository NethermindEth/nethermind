// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexCompactionResult
{
    public required string DbSizeBefore { get; init; }
    public required string DbSizeAfter { get; init; }
    public required string Elapsed { get; init; }
    public required string CompactingTime { get; init; }
}
