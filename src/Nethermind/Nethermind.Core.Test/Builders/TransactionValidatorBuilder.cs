// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionValidatorBuilder : BuilderBase<ITxValidator>
    {
        private ValidationResult _always;

        public TransactionValidatorBuilder()
        {
            TestObject = Substitute.For<ITxValidator>();
        }

        public TransactionValidatorBuilder ThatAlwaysReturnsFalse
        {
            get
            {
                _always = "Invalid";
                return this;
            }
        }

        public TransactionValidatorBuilder ThatAlwaysReturnsTrue
        {
            get
            {
                _always = ValidationResult.Success;
                return this;
            }
        }

        protected override void BeforeReturn()
        {
            TestObjectInternal.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(_always);
            TestObjectInternal.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>()).Returns(_always);
            base.BeforeReturn();
        }
    }
}
