// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    // Alex Beregszaszi, Paweł Bylica, Andrei Maiboroda, Alexey Akhunov, Christian Reitwiessner, Martin Swende, "EIP-3541: Reject new contracts starting with the 0xEF byte," Ethereum Improvement Proposals, no. 3541, March 2021. [Online serial]. Available: https://eips.ethereum.org/EIPS/eip-3541.
    public class Eip3541Tests : VirtualMachineTestsBase
    {

        [Test]
        public void Wrong_contract_creation_should_return_invalid_code_after_3541(
            [ValueSource(nameof(Eip3541TestCases))] Eip3541TestCase test,
            [ValueSource(nameof(ContractDeployments))] ContractDeployment contractDeployment)
        {
            DeployCodeAndAssertTx(test.Code, true, contractDeployment, test.WithoutAnyInvalidCodeErrors);
        }

        [Test]
        public void All_tx_should_pass_before_3541(
            [ValueSource(nameof(Eip3541TestCases))] Eip3541TestCase test,
            [ValueSource(nameof(ContractDeployments))] ContractDeployment contractDeployment)
        {
            DeployCodeAndAssertTx(test.Code, false, contractDeployment, true);
        }

        public enum ContractDeployment
        {
            CREATE2,
            CREATE,
            InitCode
        }

        public static IEnumerable<ContractDeployment> ContractDeployments
        {
            get
            {
                yield return ContractDeployment.CREATE2;
                yield return ContractDeployment.CREATE;
                yield return ContractDeployment.InitCode;
            }
        }

        public class Eip3541TestCase
        {
            public string Code { get; set; }

            public bool WithoutAnyInvalidCodeErrors { get; set; }

            public override string ToString() =>
                $"Code: {Code}";
        }

        public static IEnumerable<Eip3541TestCase> Eip3541TestCases
        {
            get
            {
                yield return new Eip3541TestCase() { Code = "0x60ef60005360016000f3", WithoutAnyInvalidCodeErrors = false };
                yield return new Eip3541TestCase() { Code = "0x60ef60005360026000f3", WithoutAnyInvalidCodeErrors = false };
                yield return new Eip3541TestCase() { Code = "0x60ef60005360036000f3", WithoutAnyInvalidCodeErrors = false };
                yield return new Eip3541TestCase() { Code = "0x60ef60005360206000f3", WithoutAnyInvalidCodeErrors = false };
                yield return new Eip3541TestCase() { Code = "0x60fe60005360016000f3", WithoutAnyInvalidCodeErrors = true };
            }
        }


        void DeployCodeAndAssertTx(string code, bool eip3541Enabled, ContractDeployment context, bool withoutAnyInvalidCodeErrors)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] salt = { 4, 5, 6 };
            byte[] byteCode = Prepare.EvmCode
                .FromCode(code)
                .Done;
            byte[] createContract;
            switch (context)
            {
                case ContractDeployment.CREATE:
                    createContract = Prepare.EvmCode.Create(byteCode, UInt256.Zero).Done;
                    break;
                case ContractDeployment.CREATE2:
                    createContract = Prepare.EvmCode.Create2(byteCode, salt, UInt256.Zero).Done;
                    break;
                default:
                    createContract = byteCode;
                    break;
            }

            _processor = new TransactionProcessor(SpecProvider, TestState, Machine, LimboLogs.Instance);
            long blockNumber = eip3541Enabled ? MainnetSpecProvider.LondonBlockNumber : MainnetSpecProvider.LondonBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, createContract);

            transaction.GasPrice = 20.GWei();
            transaction.To = null;
            transaction.Data = createContract;
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            Assert.That(tracer.ReportedActionErrors.All(x => x != EvmExceptionType.InvalidCode), Is.EqualTo(withoutAnyInvalidCodeErrors), $"Code {code}, Context {context}");
        }
    }
}
