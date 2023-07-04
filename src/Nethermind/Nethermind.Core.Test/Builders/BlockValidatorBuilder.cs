// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
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
