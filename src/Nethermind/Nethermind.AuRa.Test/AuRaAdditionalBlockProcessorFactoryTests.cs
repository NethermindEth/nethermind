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
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaAdditionalBlockProcessorFactoryTests
    {
        [TestCase(AuRaParameters.ValidatorType.List, typeof(ListValidator))]
        [TestCase(AuRaParameters.ValidatorType.Contract, typeof(ContractValidator))]
        [TestCase(AuRaParameters.ValidatorType.ReportingContract, typeof(ReportingContractValidator))]
        [TestCase(AuRaParameters.ValidatorType.Multi, typeof(MultiValidator))]
        public void returns_correct_validator_type(AuRaParameters.ValidatorType validatorType, Type expectedType)
        {
            var stateDb = Substitute.For<IDb>();
            stateDb[Arg.Any<byte[]>()].Returns((byte[]) null);
            
            var factory = new AuRaAdditionalBlockProcessorFactory(
                stateDb,
                Substitute.For<IStateProvider>(),
                Substitute.For<IAbiEncoder>(), 
                Substitute.For<ITransactionProcessor>(),
                Substitute.For<IBlockTree>(),
                Substitute.For<ILogManager>());

            var validator = new AuRaParameters.Validator()
            {
                ValidatorType = validatorType,
                Addresses = new[] {Address.Zero},
                Validators = new Dictionary<long, AuRaParameters.Validator>()
                {
                    {
                        0, new AuRaParameters.Validator()
                        {
                            ValidatorType = AuRaParameters.ValidatorType.List, Addresses = new[] {Address.SystemUser}
                        }
                    }
                }
            };
            
            var result = factory.CreateValidatorProcessor(validator);
            
            result.Should().BeOfType(expectedType);
        }
    }
}