// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.CL;

/// <summary>
/// Used both for L1 and L2 blocks
/// </summary>
public readonly record struct BlockId
{
    public ulong Number { get; private init; }
    public Hash256 Hash { get; private init; }

    public static BlockId FromL2Block(L2Block? block) => block is null
        ? new() { Number = 0, Hash = Hash256.Zero }
        : new() { Number = block.Number, Hash = block.Hash };

    public static BlockId FromExecutionPayload(ExecutionPayloadV3 executionPayload) =>
        new() { Number = (ulong)executionPayload.BlockNumber, Hash = executionPayload.BlockHash };

    public static BlockId FromL1Block(L1Block block) => new() { Number = block.Number, Hash = block.Hash };

    public static BlockId FromL1BlockInfo(L1BlockInfo blockInfo) => new() { Number = blockInfo.Number, Hash = blockInfo.BlockHash };

    public bool IsNewerThan(BlockId newBlockId) =>
        Number < newBlockId.Number;
}
