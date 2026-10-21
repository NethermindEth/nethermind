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
        /// That queue tolerates a sender still arriving: the transaction processor recovers a missing one inline
        /// and the prewarmer warms transactions as they are recovered. Tracing and one-time processing read
        /// transaction fields before execution, so they keep <see cref="RecoverData"/> and its guarantee that
        /// every sender is recovered on return.
        /// <para>
        /// The block producer's branch builder reaches this too, and its transaction picker drops a transaction
        /// whose sender is null rather than failing. It is safe because it never sees one: producing implies
        /// <c>ForceProcessing</c>, so only the block being produced is preprocessed, and its transactions come
        /// from the transaction source with senders already recovered. A new caller needs that argument or the
        /// total <see cref="RecoverData"/>.
        /// </para>
        /// </remarks>
        void RecoverDataForQueuedProcessing(Block block) => RecoverData(block);
    }
}
