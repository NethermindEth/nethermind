// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

/// <summary>
/// Used both for L1 and L2 blocks
/// </summary>
public readonly record struct BlockId
{
    public ulong Number { get; init; }
    public Hash256 Hash { get; init; }

    public static BlockId Zero => new() { Number = 0, Hash = Hash256.Zero };

    public static BlockId FromL2Block(L2Block? block) => block is null
        ? new() { Number = 0, Hash = Hash256.Zero }
        : new() { Number = block.Number, Hash = block.Hash };

    public static BlockId FromExecutionPayload(ExecutionPayloadV3 executionPayload) =>
        new() { Number = (ulong)executionPayload.BlockNumber, Hash = executionPayload.BlockHash };

    public static BlockId FromL1Block(L1Block block) => new() { Number = block.Number, Hash = block.Hash };

    public static BlockId FromL1BlockInfo(L1BlockInfo blockInfo) => new() { Number = blockInfo.Number, Hash = blockInfo.BlockHash };

    public bool IsOlderThan(BlockId newBlockId) =>
        Number < newBlockId.Number;

    public bool IsOlderThan(ulong otherBlockNumber) =>
        Number < otherBlockNumber;

    public bool IsNewerThan(ulong otherBlockNumber) => Number > otherBlockNumber;

    public override string ToString()
    {
        return $"{Number} ({Hash.ToShortString()})";
    }
}
