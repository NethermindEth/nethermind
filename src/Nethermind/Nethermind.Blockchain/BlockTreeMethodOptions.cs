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

namespace Nethermind.Blockchain;

/// <summary>
/// This class has been introduced for performance reasons only (in order to minimize expensive DB lookups where not necessary).
/// </summary>
[Flags]
public enum BlockTreeLookupOptions
{
    None = 0,
    TotalDifficultyNotNeeded = 1,
    RequireCanonical = 2,
    All = 3
}

[Flags]
public enum BlockTreeInsertOptions
{
    None = 0,
    TotalDifficultyNotNeeded = 1,
    SkipUpdateBestPointers = 2,
    NotOnMainChain = 4,
    UpdateBeaconPointers = 8,
    AddBeaconMetadata = 16,
    MoveToBeaconMainChain = 32,
    
    BeaconBlockInsert = TotalDifficultyNotNeeded | SkipUpdateBestPointers | UpdateBeaconPointers | AddBeaconMetadata | MoveToBeaconMainChain | NotOnMainChain
}

[Flags]
public enum BlockTreeSuggestOptions
{
    None = 0,
    ShouldProcess = 1,
    FillBeaconBlock = 2
}
