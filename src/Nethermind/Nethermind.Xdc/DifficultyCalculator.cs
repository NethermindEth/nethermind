// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc;

internal class DifficultyCalculator : IDifficultyCalculator
{
    public UInt256 Calculate(BlockHeader header, BlockHeader parent)
    {
        return XdcConstants.DifficultyDefault;
    }
}
