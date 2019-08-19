using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;
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
        public void Returns_correct_validator_type(AuRaParameters.ValidatorType validatorType, Type expectedType)
        {
            var factory = new AuRaAdditionalBlockProcessorFactory(
                Substitute.For<IStateProvider>(),
                Substitute.For<IAbiEncoder>(),
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