// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.SkipIndexedBlockInfo;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.Era1.Test;

/// <summary>
/// Test-only <see cref="ISkipIndexedBlockInfoStore"/> that returns a per-header total difficulty
/// derived statically from the header alone. Returns <c>max(header.Difficulty, DefaultDifficulty)</c>
/// so EraWriter's invariant (TD &gt;= block.Difficulty) holds without wiring a real store.
/// </summary>
public sealed class StaticTotalDifficultySkipIndexedBlockInfoStore : ISkipIndexedBlockInfoStore
{
    public static readonly StaticTotalDifficultySkipIndexedBlockInfoStore Instance = new();

    private StaticTotalDifficultySkipIndexedBlockInfoStore() { }

    public UInt256? GetTotalDifficulty(long blockNumber, in ValueHash256 blockHash) =>
        BlockHeaderBuilder.DefaultDifficulty;

    public UInt256? GetTotalDifficulty(BlockHeader? header)
    {
        if (header is null) return null;
        return header.Difficulty > BlockHeaderBuilder.DefaultDifficulty
            ? header.Difficulty
            : BlockHeaderBuilder.DefaultDifficulty;
    }

    public ValueHash256? GetAncestorAt(long blockNumber, in ValueHash256 blockHash, long ancestorBlockNumber) => null;
}
