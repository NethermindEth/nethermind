// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public interface ITotalDifficultyStrategy
{
    UInt256 ParentTotalDifficulty(BlockHeader header);
}

public sealed class CumulativeTotalDifficultyStrategy : ITotalDifficultyStrategy
{
    public UInt256 ParentTotalDifficulty(BlockHeader header)
    {
        return (header.TotalDifficulty ?? 0) - header.Difficulty;
    }
}

public sealed class FixedTotalDifficultyStrategy(
    ITotalDifficultyStrategy strategy,
    long fixesBlockNumber,
    UInt256 toTotalDifficulty
) : ITotalDifficultyStrategy
{
    public UInt256 ParentTotalDifficulty(BlockHeader header)
    {
        return header.Number > 0 && header.Number - 1 == fixesBlockNumber
            ? toTotalDifficulty
            : strategy.ParentTotalDifficulty(header);
    }
}
