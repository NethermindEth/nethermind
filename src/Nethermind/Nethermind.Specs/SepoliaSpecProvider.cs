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
//

using System;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs;

public class SepoliaSpecProvider : ISpecProvider
{
    private long? _theMergeBlock = null;
    private UInt256? _terminalTotalDifficulty = 17000000000000000;

    public void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null)
    {
        if (blockNumber != null)
            _theMergeBlock = blockNumber;
        if (terminalTotalDifficulty != null)
            _terminalTotalDifficulty = terminalTotalDifficulty;
    }

    public long? MergeBlockNumber => _theMergeBlock;
    public UInt256? TerminalTotalDifficulty => _terminalTotalDifficulty;
    public IReleaseSpec GenesisSpec => London.Instance;

    public IReleaseSpec GetSpec(long blockNumber) => London.Instance;

    public long? DaoBlockNumber => null;


    public ulong ChainId => Core.ChainId.Rinkeby;

    public long[] TransitionBlocks { get; } = { 1735371 };

    private SepoliaSpecProvider() { }

    public static readonly SepoliaSpecProvider Instance = new();
}
