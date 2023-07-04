// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus
{
    public interface IDifficultyCalculator
    {
        UInt256 Calculate(BlockHeader header, BlockHeader parent);
    }
}
