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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public class Pivot : IPivot
{
    private readonly ISyncConfig _syncconfig;
    
    public Pivot(ISyncConfig syncConfig)
    {
        _syncconfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));

        PivotNumber = _syncconfig.PivotNumberParsed;
        PivotHash = _syncconfig.PivotHashParsed;
        PivotTotalDifficulty = _syncconfig.PivotTotalDifficultyParsed;
        PivotDestinationNumber = 0L;
    }
    
    public long PivotNumber { get; }

    public Keccak? PivotHash { get; }

    public UInt256? PivotTotalDifficulty { get; }
        
    public long PivotDestinationNumber { get; }
}
