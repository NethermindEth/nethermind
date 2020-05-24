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

namespace Nethermind.Blockchain.Processing
{
    [Flags]
    public enum ProcessingOptions
    {
        None = 0,
        ReadOnlyChain = 1,
        ForceProcessing = 2,
        StoreReceipts = 4,
        NoValidation = 8,
        IgnoreParentNotOnMainChain = 16,
        DoNotVerifyNonce = 32,
        DoNotUpdateHead = 64,
        DumpParityTraces = 128,
        DumpGetTraces = 256,
        All = 511,
        ProducingBlock = NoValidation | ReadOnlyChain,
        Trace = ForceProcessing | ReadOnlyChain | DoNotVerifyNonce | NoValidation,
        Beam = IgnoreParentNotOnMainChain | DoNotUpdateHead
    }

    public static class ProcessingOptionsExtensions
    {
        public static bool IsReadOnly(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ReadOnlyChain) == ProcessingOptions.ReadOnlyChain;
        public static bool IsNotReadOnly(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ReadOnlyChain) != ProcessingOptions.ReadOnlyChain;
        public static bool IsProducingBlock(this ProcessingOptions processingOptions) => (processingOptions & ProcessingOptions.ProducingBlock) == ProcessingOptions.ProducingBlock;
    }
}