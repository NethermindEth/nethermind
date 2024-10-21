// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaAdditionalBlockProcessorFactoryTests
    {
        [TestCase(ValidatorType.List, typeof(ListBasedValidator))]
        [TestCase(ValidatorType.Contract, typeof(ContractBasedValidator))]
        [TestCase(ValidatorType.ReportingContract, typeof(ReportingContractBasedValidator))]
        [TestCase(ValidatorType.Multi, typeof(MultiValidator))]
        public void returns_correct_validator_type(ValidatorType validatorType, Type expectedType)
        {
            AuRaValidatorFactory factory = new(Substitute.For<IAbiEncoder>(),
                Substitute.For<IWorldState>(),
                Substitute.For<ITransactionProcessor>(),
                Substitute.For<IBlockTree>(),
                Substitute.For<IReadOnlyTxProcessorSource>(),
                Substitute.For<IReceiptStorage>(),
                Substitute.For<IValidatorStore>(),
                Substitute.For<IAuRaBlockFinalizationManager>(),
                Substitute.For<ITxSender>(),
                Substitute.For<ITxPool>(),
                new BlocksConfig(),
                LimboLogs.Instance,
                Substitute.For<ISigner>(),
                Substitute.For<ISpecProvider>(),

                Substitute.For<IGasPriceOracle>(),
                new ReportingContractBasedValidator.Cache(), long.MaxValue);

            Validator validator = new()
            {
                ValidatorType = validatorType,
                Addresses = new[] { Address.Zero },
                Validators = new Dictionary<long, Validator>()
                {
                    {
                        0, new Validator()
                        {
                            ValidatorType = ValidatorType.List, Addresses = new[] {Address.SystemUser}
                        }
                    }
                }
            };

            IAuRaValidator result = factory.CreateValidatorProcessor(validator);

            result.Should().BeOfType(expectedType);
        }
    }
}
