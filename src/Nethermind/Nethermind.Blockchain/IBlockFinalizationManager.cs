//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public interface IBlockFinalizationManager : IDisposable
    {
        /// <summary>
        /// Last level that was finalize while processing blocks. This level will not be reorganised.
        /// </summary>
        long LastFinalizedBlockLevel { get; }
        event EventHandler<FinalizeEventArgs> BlocksFinalized;
        
        /// <summary>
        /// Get last level finalized by certain block hash.
        /// </summary>
        /// <param name="blockHash">Hash of block</param>
        /// <returns>Last level that was finalized by block hash.</returns>
        /// <remarks>This is used when we have nonconsecutive block processing, like just switching from Fast to Full sync or when producing blocks. It is used when trying to find a non-finalized InitChange event.</remarks>
        long GetLastLevelFinalizedBy(Keccak blockHash);
    }
}
