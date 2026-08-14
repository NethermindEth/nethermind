// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus
{
    public class ConstantDifficulty(in UInt256 constantDifficulty) : IDifficultyCalculator
    {
        private readonly UInt256 _constantDifficulty = constantDifficulty;

        public static readonly IDifficultyCalculator Zero = new ConstantDifficulty(UInt256.Zero);

        public static readonly IDifficultyCalculator One = new ConstantDifficulty(UInt256.One);

        public UInt256 Calculate(BlockHeader header, BlockHeader parent) => _constantDifficulty;
    }
}
