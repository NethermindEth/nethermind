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
    }
}
