//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class GoerliSpecProvider : ISpecProvider
    {
        public static readonly GoerliSpecProvider Instance = new();
        private GoerliSpecProvider() { }

        private long? _theMergeBlock = null;
        private UInt256? _terminalTotalDifficulty = 10790000;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber != null)
                _theMergeBlock = blockNumber;
            if (terminalTotalDifficulty != null)
                _terminalTotalDifficulty = terminalTotalDifficulty;
        }

        public long? MergeBlockNumber => _theMergeBlock;
        public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
        public IReleaseSpec GenesisSpec { get; } = ConstantinopleFix.Instance;

        private IReleaseSpec IstanbulNoBomb { get; } = Istanbul.Instance;

        private IReleaseSpec BerlinNoBomb { get; } = Berlin.Instance;

        private IReleaseSpec LondonNoBomb { get; } = London.Instance;

        public IReleaseSpec GetSpec(long blockNumber) =>
            blockNumber switch
            {
                < IstanbulBlockNumber => GenesisSpec,
                < BerlinBlockNumber => IstanbulNoBomb,
                < LondonBlockNumber => BerlinNoBomb,
                _ => LondonNoBomb
            };

        public long? DaoBlockNumber => null;
        public const long IstanbulBlockNumber = 1_561_651;
        public const long BerlinBlockNumber = 4_460_644;
        public const long LondonBlockNumber = 5_062_605;
        public ulong ChainId => Core.ChainId.Goerli;

        public long[] TransitionBlocks { get; } =
        {
            IstanbulBlockNumber,
            BerlinBlockNumber,
            LondonBlockNumber
        };
    }
}
