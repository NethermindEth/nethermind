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

using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    public class RopstenSpecProvider : ISpecProvider
    {
        private long? _theMergeBlock;
        private UInt256? _terminalTotalDifficulty = 50000000000000000;

        public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
        {
            if (blockNumber != null)
                _theMergeBlock = blockNumber;
            if (terminalTotalDifficulty != null)
                _terminalTotalDifficulty = terminalTotalDifficulty;
        }

        public long? MergeBlockNumber => _theMergeBlock;
        public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
        public IReleaseSpec GenesisSpec => TangerineWhistle.Instance;

        public IReleaseSpec GetSpec(long blockNumber) =>
            blockNumber switch
            {
                < SpuriousDragonBlockNumber => TangerineWhistle.Instance,
                < ByzantiumBlockNumber => SpuriousDragon.Instance,
                < ConstantinopleBlockNumber => Byzantium.Instance,
                < ConstantinopleFixBlockNumber => Constantinople.Instance,
                < IstanbulBlockNumber => ConstantinopleFix.Instance,
                < MuirGlacierBlockNumber => Istanbul.Instance,
                < BerlinBlockNumber => MuirGlacier.Instance,
                < LondonBlockNumber => Berlin.Instance,
                _ => London.Instance
            };

        public long? DaoBlockNumber => null;
        public const long SpuriousDragonBlockNumber  = 10;
        public const long ByzantiumBlockNumber  = 1_700_000;
        public const long ConstantinopleBlockNumber  = 4_230_000;
        public const long ConstantinopleFixBlockNumber  = 4_939_394;
        public const long IstanbulBlockNumber  = 6_485_846;
        public const long MuirGlacierBlockNumber  = 7_117_117;
        public const long BerlinBlockNumber  = 9_812_189;
        public const long LondonBlockNumber = 10_499_401;

        public ulong ChainId => Core.ChainId.Ropsten;
        public long[] TransitionBlocks => new[]
        {
            SpuriousDragonBlockNumber,
            ByzantiumBlockNumber,
            ConstantinopleBlockNumber,
            ConstantinopleFixBlockNumber,
            IstanbulBlockNumber,
            MuirGlacierBlockNumber,
            BerlinBlockNumber,
            LondonBlockNumber
        };

        private RopstenSpecProvider() { }

        public static readonly RopstenSpecProvider Instance = new();
    }
}
