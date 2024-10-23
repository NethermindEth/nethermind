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
