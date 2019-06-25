/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading;
using Nethermind.Core;

namespace Nethermind.Blockchain.Validators
{
    public class AlwaysValidBlockValidator : IBlockValidator
    {
        private static AlwaysValidBlockValidator _instance;

        public static AlwaysValidBlockValidator Instance
            => LazyInitializer.EnsureInitialized(ref _instance, () => new AlwaysValidBlockValidator());

        public bool ValidateHash(BlockHeader header)
        {
            return true;
        }

        public bool ValidateHeader(BlockHeader header, BlockHeader parent, bool isOmmer)
        {
            return true;
        }

        public bool ValidateHeader(BlockHeader header, bool isOmmer)
        {
            return true;
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            return true;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            return true;
        }
    }
}