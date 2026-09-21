// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public interface IBlockPreprocessorStep
    {
        /// <summary>
        /// Called before the block is put into the processing queue. Example would be recovering transaction
        /// sender addresses for each transaction.
        /// RECOVERY QUEUE - BLOCK N - BLOCK (N+1) - BLOCK (N+2) - ...
        /// RecoverData
        /// PROCESSING QUEUE - BLOCK (N-2) - BLOCK (N-1) - ...
        /// ProcessBlock
        /// </summary>
        /// <param name="block">Block to change / enrich before processing.</param>
        void RecoverData(Block block);

        /// <summary>
        /// <see cref="RecoverData"/> for the main processing queue, which may leave part of the work to a
        /// recovery already running for the same transactions.
        /// </summary>
        /// <remarks>
        /// Only that queue tolerates a sender still arriving: the transaction processor recovers a missing one
        /// inline and the prewarmer warms transactions as they are recovered. Every other caller — tracing,
        /// one-time processing — reads transaction fields before execution and so keeps <see cref="RecoverData"/>,
        /// which returns with every sender recovered.
        /// </remarks>
        void RecoverDataForQueuedProcessing(Block block) => RecoverData(block);
    }
}
