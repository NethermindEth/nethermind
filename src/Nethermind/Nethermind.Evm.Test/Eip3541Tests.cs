//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    // Alex Beregszaszi, Paweł Bylica, Andrei Maiboroda, Alexey Akhunov, Christian Reitwiessner, Martin Swende, "EIP-3541: Reject new contracts starting with the 0xEF byte," Ethereum Improvement Proposals, no. 3541, March 2021. [Online serial]. Available: https://eips.ethereum.org/EIPS/eip-3541.
    public class Eip3541Tests : VirtualMachineTestsBase
    {
        const long LondonTestBlockNumber = 4_370_000;
        protected override ISpecProvider SpecProvider
        {
            get
            {
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Is<long>(x => x >= LondonTestBlockNumber)).Returns(London.Instance);
                specProvider.GetSpec(Arg.Is<long>(x => x < LondonTestBlockNumber)).Returns(Berlin.Instance);
                return specProvider;
            }
        }
        
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
            
            byte[] salt = {4, 5, 6};
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
            
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            long blockNumber = eip3541Enabled ? LondonTestBlockNumber : LondonTestBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, createContract);
        
            transaction.GasPrice = 20.GWei();
            transaction.To = null;
            transaction.Data = createContract;
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            Assert.AreEqual(withoutAnyInvalidCodeErrors, tracer.ReportedActionErrors.All(x => x != EvmExceptionType.InvalidCode),$"Code {code}, Context {context}");
        }
    }
}
