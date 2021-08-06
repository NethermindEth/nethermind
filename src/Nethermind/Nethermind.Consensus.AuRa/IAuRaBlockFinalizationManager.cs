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

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa
{
    public interface IAuRaBlockFinalizationManager : IBlockFinalizationManager
    {
        /// <summary>
        /// Get last level finalized by certain block hash.
        /// </summary>
        /// <param name="blockHash">Hash of block</param>
        /// <returns>Last level that was finalized by block hash.</returns>
        /// <remarks>This is used when we have nonconsecutive block processing, like just switching from Fast to Full sync or when producing blocks. It is used when trying to find a non-finalized InitChange event.</remarks>
        long GetLastLevelFinalizedBy(Keccak blockHash);

        /// <summary>
        /// Gets level ath which the certain level was finalized.
        /// </summary>
        /// <param name="level">Level to check when was finalized.</param>
        /// <returns>Level at which finalization happened. Null if checked level is not yet finalized.</returns>
        long? GetFinalizationLevel(long level);
    }
}
