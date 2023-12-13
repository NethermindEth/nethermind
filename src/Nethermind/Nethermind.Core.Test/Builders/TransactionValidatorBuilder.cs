// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionValidatorBuilder : BuilderBase<ITxValidator>
    {
        private bool _alwaysTrue;

        public TransactionValidatorBuilder()
        {
            TestObject = Substitute.For<ITxValidator>();
        }

        public TransactionValidatorBuilder ThatAlwaysReturnsFalse
        {
            get
            {
                _alwaysTrue = false;
                return this;
            }
        }

        public TransactionValidatorBuilder ThatAlwaysReturnsTrue
        {
            get
            {
                _alwaysTrue = true;
                return this;
            }
        }

        protected override void BeforeReturn()
        {
            TestObjectInternal.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(_alwaysTrue);
            TestObjectInternal.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(_alwaysTrue);
            base.BeforeReturn();
        }
    }
}
