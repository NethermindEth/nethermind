// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using NSubstitute;

namespace Nethermind.Core.Test.Builders
{
    public class SealValidatorBuilder : BuilderBase<ISealValidator>
    {
        private bool _alwaysTrue;

        public SealValidatorBuilder()
        {
            TestObject = Substitute.For<ISealValidator>();
        }

        public SealValidatorBuilder ThatAlwaysReturnsFalse
        {
            get
            {
                _alwaysTrue = false;
                return this;
            }
        }

        public SealValidatorBuilder ThatAlwaysReturnsTrue
        {
            get
            {
                _alwaysTrue = true;
                return this;
            }
        }

        protected override void BeforeReturn()
        {
            TestObjectInternal.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns(_alwaysTrue);
            TestObjectInternal.ValidateParams(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>()).Returns(_alwaysTrue);
            base.BeforeReturn();
        }
    }
}
