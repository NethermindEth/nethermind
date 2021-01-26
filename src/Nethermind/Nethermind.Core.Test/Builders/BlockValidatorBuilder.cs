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

using Nethermind.Blockchain.Validators;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class BlockValidatorBuilder : BuilderBase<IBlockValidator>
    {
        private bool _alwaysTrue;

        public BlockValidatorBuilder()
        {
            TestObject = Substitute.For<IBlockValidator>();
        }

        public BlockValidatorBuilder ThatAlwaysReturnsFalse
        {
            get
            {
                _alwaysTrue = false;
                return this;
            }
        }

        public BlockValidatorBuilder ThatAlwaysReturnsTrue
        {
            get
            {
                _alwaysTrue = true;
                return this;
            }
        }

        protected override void BeforeReturn()
        {
            TestObjectInternal.ValidateSuggestedBlock(Arg.Any<Block>()).Returns(_alwaysTrue);
            TestObjectInternal.ValidateProcessedBlock(Arg.Any<Block>(), Arg.Any<TxReceipt[]>(), Arg.Any<Block>()).Returns(_alwaysTrue);
            base.BeforeReturn();
        }
    }
}
