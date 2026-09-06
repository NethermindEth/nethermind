// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Builders
{
    [TestFixture]
    public class TransactionValidatorBuilderTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void Should_configure_all_validation_overloads(bool shouldSucceed)
        {
            TransactionValidatorBuilder builder = new();
            ITxValidator validator = (shouldSucceed
                ? builder.ThatAlwaysReturnsTrue
                : builder.ThatAlwaysReturnsFalse).TestObject;
            Transaction transaction = new();
            IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(validator.IsWellFormed(transaction, releaseSpec).AsBool(), Is.EqualTo(shouldSucceed));
                Assert.That(validator.IsWellFormed(transaction, releaseSpec, 1).AsBool(), Is.EqualTo(shouldSucceed));
                Assert.That(
                    validator.IsWellFormed(transaction, releaseSpec, 1, TxValidationOptions.SkipBlobProofs).AsBool(),
                    Is.EqualTo(shouldSucceed));
            }
        }
    }
}
