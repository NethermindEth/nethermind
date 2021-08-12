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

using System.Threading;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Validators
{
    public class Always : IBlockValidator, ISealValidator, IOmmersValidator, ITxValidator
    {
        private readonly bool _result;

        private Always(bool result)
        {
            _result = result;
        }
        
        // ReSharper disable once NotNullMemberIsNotInitialized
        private static Always _valid;

        public static Always Valid
            => LazyInitializer.EnsureInitialized(ref _valid, () => new Always(true));
        
        // ReSharper disable once NotNullMemberIsNotInitialized
        private static Always _invalid;
        
        public static Always Invalid
            => LazyInitializer.EnsureInitialized(ref _invalid, () => new Always(false));

        public bool ValidateHash(BlockHeader header)
        {
            return _result;
        }

        public bool Validate(BlockHeader blockHeader, BlockHeader parent, bool isOmmer = false)
        {
            return _result;
        }

        public bool Validate(BlockHeader blockHeader, bool isOmmer = false)
        {
            return _result;
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            return _result;
        }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            return _result;
        }
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            return _result;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            return _result;
        }

        public bool Validate(BlockHeader header, BlockHeader[] ommers)
        {
            return _result;
        }

        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        {
            return _result;
        }
    }
}
