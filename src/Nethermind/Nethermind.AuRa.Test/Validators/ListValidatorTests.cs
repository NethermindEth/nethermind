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
using System.Numerics;
using System.Runtime.Serialization.Formatters;
using FluentAssertions;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ListValidatorTests
    {
        private const string Include1 = "0xffffffffffffffffffffffffffffffffffffffff";
        private const string Include2 = "0xfffffffffffffffffffffffffffffffffffffffe";
        
        [TestCase(Include1, ExpectedResult = true)]
        [TestCase(Include2, ExpectedResult = true)]
        [TestCase("0xAAfffffffffffffffffffffffffffffffffffffe", ExpectedResult = false)]
        [TestCase("0xfffffffffffffffffffffffffffffffffffffffd", ExpectedResult = false)]
        public bool should_validate_correctly(string address)
        {
            var validator = new ListValidator(
                new AuRaParameters.Validator()
                {
                    Addresses = new[] {new Address(Include1), new Address(Include2), }
                });

            return validator.IsValidSealer(new Address(address));
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new ListValidator(null);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_wrong_validator_type()
        {
            Action act = () => new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.Contract,
                    Addresses = new[] {Address.Zero}
                });
            
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_empty_addresses()
        {
            Action act = () => new ListValidator(
                new AuRaParameters.Validator()
                {
                    ValidatorType = AuRaParameters.ValidatorType.List
                });
            
            act.Should().Throw<ArgumentException>();
        }
    }
}