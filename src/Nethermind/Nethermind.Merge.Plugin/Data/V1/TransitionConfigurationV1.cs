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

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Data.V1;

/// <summary>
/// Result of call.
/// 
/// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#transitionconfigurationv1"/>
/// </summary>
public class TransitionConfigurationV1
{
    /// <summary>
    /// Maps on the TERMINAL_TOTAL_DIFFICULTY parameter of EIP-3675
    /// </summary>
    public UInt256? TerminalTotalDifficulty { get; set; }
    
    /// <summary>
    /// Maps on TERMINAL_BLOCK_HASH parameter of EIP-3675
    /// </summary>
    public Keccak TerminalBlockHash { get; set; } = Keccak.Zero;
    
    /// <summary>
    /// Maps on TERMINAL_BLOCK_NUMBER parameter of EIP-3675
    /// </summary>
    public long TerminalBlockNumber { get; set; }
}
