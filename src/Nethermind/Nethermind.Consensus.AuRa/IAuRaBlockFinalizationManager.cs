// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
