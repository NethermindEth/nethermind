// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class HeaderValidatorBuilder : BuilderBase<IHeaderValidator>
    {
        private bool _alwaysTrue;

        public HeaderValidatorBuilder()
        {
            TestObject = Substitute.For<IHeaderValidator>();
        }

        public HeaderValidatorBuilder ThatAlwaysReturnsFalse
        {
            get
            {
                _alwaysTrue = false;
                return this;
            }
        }

        public HeaderValidatorBuilder ThatAlwaysReturnsTrue
        {
            get
            {
                _alwaysTrue = true;
                return this;
            }
        }

        protected override void BeforeReturn()
        {
            TestObjectInternal.Validate(Arg.Any<BlockHeader>()).Returns(_alwaysTrue);
            base.BeforeReturn();
        }
    }
}
