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

using System;
<<<<<<< HEAD
using System.Collections;
using System.Collections.Generic;
using System.Linq;
=======
>>>>>>> test squash
using System.Numerics;
using System.Runtime.Serialization.Formatters;
using FluentAssertions;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
<<<<<<< HEAD
using Nethermind.Core.Test.Builders;
=======
>>>>>>> test squash
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ListValidatorTests
    {
<<<<<<< HEAD
        private ListValidator GetListValidator(params Address[] address)
        {
            var logManager = Substitute.For<ILogManager>();
            var validator = new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.List,
                    Addresses = address
                }, logManager);
            
            return validator;
        }

        private static IEnumerable ValidateTestCases
        {
            get
            {
                yield return new TestCaseData(TestItem.AddressA, 0L) {ExpectedResult = true};
                yield return new TestCaseData(TestItem.AddressA, 1L) {ExpectedResult = false};
                yield return new TestCaseData(TestItem.AddressB, 1L) {ExpectedResult = true};
                yield return new TestCaseData(TestItem.AddressB, 0L) {ExpectedResult = false};
                yield return new TestCaseData(TestItem.AddressC, 0L) {ExpectedResult = false};
                yield return new TestCaseData(TestItem.AddressC, 1L) {ExpectedResult = false};
            }
        }
        
        [TestCaseSource(nameof(ValidateTestCases))]
        public bool should_validate_correctly(Address address, long index) =>
            GetListValidator(TestItem.AddressA, TestItem.AddressB)
                .IsValidSealer(address, index);

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(10)]
        public void should_get_current_sealers_count(int validatorCount)
        {
            GetListValidator(TestItem.Addresses.Take(validatorCount).ToArray())
                .CurrentSealersCount.Should().Be(validatorCount);
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
            GetListValidator(TestItem.Addresses.Take(validatorCount).ToArray()).MinSealersForFinalization;
=======
        private ILogManager _logManager;
        private const string Include1 = "0xffffffffffffffffffffffffffffffffffffffff";
        private const string Include2 = "0xfffffffffffffffffffffffffffffffffffffffe";
        
        [TestCase(Include1, 0L, ExpectedResult = true)]
        [TestCase(Include1, 1L, ExpectedResult = false)]
        [TestCase(Include2, 1L, ExpectedResult = true)]
        [TestCase(Include2, 0L, ExpectedResult = false)]
        [TestCase("0xAAfffffffffffffffffffffffffffffffffffffe", 0L, ExpectedResult = false)]
        [TestCase("0xfffffffffffffffffffffffffffffffffffffffd", 1L, ExpectedResult = false)]
        public bool should_validate_correctly(string address, long index)
        {
            _logManager = Substitute.For<ILogManager>();
            var validator = new ListValidator(
                new AuRaParameters.Validator()
                {
                    Addresses = new[] {new Address(Include1), new Address(Include2), }
                }, _logManager);

            return validator.IsValidSealer(new Address(address), index);
        }
>>>>>>> test squash

        [Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
<<<<<<< HEAD
            var logManager = Substitute.For<ILogManager>();
            Action act = () => new ListValidator(null, logManager); 
=======
            Action act = () => new ListValidator(null, _logManager);
>>>>>>> test squash
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_empty_addresses()
        {
<<<<<<< HEAD
            Action act = () => GetListValidator();
=======
            Action act = () => new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.List
                }, _logManager);
            
>>>>>>> test squash
            act.Should().Throw<ArgumentException>();
        }
    }
}