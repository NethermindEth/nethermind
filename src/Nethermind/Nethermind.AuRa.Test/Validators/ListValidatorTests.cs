// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ListValidatorTests
    {
        private ValidSealerStrategy _validSealerStrategy;

        private ListBasedValidator GetListValidator(params Address[] address)
        {
            LimboLogs logManager = LimboLogs.Instance;
            _validSealerStrategy = new ValidSealerStrategy();
            ListBasedValidator validator = new(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.List,
                    Addresses = address
                }, _validSealerStrategy, Substitute.For<IValidatorStore>(), logManager, 1);

            return validator;
        }

        private static IEnumerable ValidateTestCases
        {
            get
            {
                yield return new TestCaseData(TestItem.AddressA, 0L) { ExpectedResult = true };
                yield return new TestCaseData(TestItem.AddressA, 1L) { ExpectedResult = false };
                yield return new TestCaseData(TestItem.AddressB, 1L) { ExpectedResult = true };
                yield return new TestCaseData(TestItem.AddressB, 0L) { ExpectedResult = false };
                yield return new TestCaseData(TestItem.AddressC, 0L) { ExpectedResult = false };
                yield return new TestCaseData(TestItem.AddressC, 1L) { ExpectedResult = false };
            }
        }

        [TestCaseSource(nameof(ValidateTestCases))]
        public bool should_validate_correctly(Address address, long index) =>
            _validSealerStrategy.IsValidSealer(GetListValidator(TestItem.AddressA, TestItem.AddressB).Validators, address, index);

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(10)]
        public void should_get_current_sealers_count(int validatorCount)
        {
            GetListValidator(TestItem.Addresses.Take(validatorCount).ToArray()).Validators.Length.Should().Be(validatorCount);
        }

        [TestCase(1, ExpectedResult = 1)]
        [TestCase(2, ExpectedResult = 2)]
        [TestCase(3, ExpectedResult = 2)]
        [TestCase(4, ExpectedResult = 3)]
        [TestCase(5, ExpectedResult = 3)]
        [TestCase(6, ExpectedResult = 4)]
        [TestCase(9, ExpectedResult = 5)]
        [TestCase(10, ExpectedResult = 6)]
        [TestCase(100, ExpectedResult = 51)]
        public int should_get_min_sealers_for_finalization(int validatorCount) =>
            GetListValidator(TestItem.Addresses.Take(validatorCount).ToArray()).Validators.MinSealersForFinalization();

        [Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            LimboLogs logManager = LimboLogs.Instance;
            Action act = () => new ListBasedValidator(null, new ValidSealerStrategy(), Substitute.For<IValidatorStore>(), logManager, 1);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentException_on_empty_addresses()
        {
            Action act = () => GetListValidator();
            act.Should().Throw<ArgumentException>();
        }
    }
}
