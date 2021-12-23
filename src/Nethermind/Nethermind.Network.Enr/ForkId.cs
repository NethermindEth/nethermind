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

namespace Nethermind.Network.Enr;

/// <summary>
/// Represents the Ethereum Fork ID (hash of a list of fork block numbers and the next fork block number).
/// </summary>
public struct ForkId
{
    public ForkId(byte[] forkHash, long nextBlock)
    {
        ForkHash = forkHash;
        NextBlock = nextBlock;
    }
    
    /// <summary>
    /// Hash of a list of the past fork block numbers.
    /// </summary>
    public byte[] ForkHash { get; set; }
    
    /// <summary>
    /// Block number of the next known fork (or 0 if no fork is expected).
    /// </summary>
    public long NextBlock { get; set; }
}
