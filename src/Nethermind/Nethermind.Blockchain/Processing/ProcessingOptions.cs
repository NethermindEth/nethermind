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

using System;

namespace Nethermind.Blockchain.Processing
{
    [Flags]
    public enum ProcessingOptions
    {
        None = 0,
        
        /// <summary>
        /// It will not update the storage data (will discard any changes).
        /// </summary>
        ReadOnlyChain = 1,
        
        /// <summary>
        /// Will process the block even if it was processed in the past.
        /// </summary>
        ForceProcessing = 2,
        
        /// <summary>
        /// After processing will store all tx receipts in the storage.
        /// </summary>
        StoreReceipts = 4,
        
        /// <summary>
        /// Will process the block even if it is invalid.
        /// </summary>
        NoValidation = 8,
        
        /// <summary>
        /// Allows to process the block even if its parent is not on canonical chain.
        /// </summary>
        IgnoreParentNotOnMainChain = 16,
        
        /// <summary>
        /// Does not verify transaction nonces during processing.
        /// </summary>
        DoNotVerifyNonce = 32,
        
        /// <summary>
        /// After processing it will not update the block tree head even if the processed block has the highest
        /// total difficulty.
        /// </summary>
        DoNotUpdateHead = 64,
        All = 127,
        
        /// <summary>
        /// Combination of switches for block producers when they preprocess block for state root calculation.
        /// </summary>
        ProducingBlock = NoValidation | ReadOnlyChain | ForceProcessing | DoNotUpdateHead,
        
        /// <summary>
        /// EVM tracing needs to process blocks without storing the data on chain.
        /// </summary>
        Trace = ForceProcessing | ReadOnlyChain | DoNotVerifyNonce | NoValidation,
        
        /// <summary>
        /// Switches used by the beam sync processor.
        /// </summary>
        Beam = IgnoreParentNotOnMainChain | DoNotUpdateHead,
        
        EthereumMerge = ReadOnlyChain | ForceProcessing | DoNotUpdateHead | IgnoreParentNotOnMainChain
    }

    public static class ProcessingOptionsExtensions
    {
        public static bool IsReadOnly(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ReadOnlyChain) == ProcessingOptions.ReadOnlyChain;
        public static bool IsNotReadOnly(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ReadOnlyChain) != ProcessingOptions.ReadOnlyChain;
        public static bool IsProducingBlock(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ProducingBlock) == ProcessingOptions.ProducingBlock;
    }
}
