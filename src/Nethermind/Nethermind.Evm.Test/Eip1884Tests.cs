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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip1884Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;
        
        [Test]
        public void after_istanbul_selfbalance_opcode_puts_current_address_balance_onto_the_stack()
        {
            byte[] contractCode = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
                    
            Keccak codeHash = TestState.UpdateCode(contractCode);
            
            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            TestState.UpdateCodeHash(TestItem.AddressC, codeHash, Spec);
            
            TestState.CreateAccount(TestItem.AddressD, 1.Ether());
            TestState.UpdateCodeHash(TestItem.AddressD, codeHash, Spec);
            
            var code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .DelegateCall(TestItem.AddressD, 50000)
                .Op(Instruction.SELFBALANCE)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .Done;
            
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + 2 * GasCostOf.CallEip150 + 24 + 21 + GasCostOf.VeryLow + 3 * GasCostOf.SelfBalance + 3 * GasCostOf.SSet);
            UInt256 balanceB = TestState.GetBalance(TestItem.AddressB);
            UInt256 balanceC = TestState.GetBalance(TestItem.AddressC);
            AssertStorage(new StorageCell(TestItem.AddressB, UInt256.Zero), balanceB);
            AssertStorage(new StorageCell(TestItem.AddressB, UInt256.One), balanceB);
            AssertStorage(new StorageCell(TestItem.AddressC, UInt256.Zero), balanceC);
        }

        [Test]
        public void after_istanbul_extcodehash_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;

            var result = Execute(code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.ExtCodeHashEip1884);
        }
        
        [Test]
        public void after_istanbul_balance_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            var result = Execute(code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.BalanceEip1884);
        }
        
        [Test]
        public void after_istanbul_sload_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.SLOAD)
                .Done;

            var result = Execute(code);
            AssertGas(result, 21000 + 2 * GasCostOf.VeryLow + GasCostOf.SLoadEip1884);
        }
        
        [Test]
        public void just_before_istanbul_extcodehash_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.EXTCODEHASH)
                .Done;

            var result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.ExtCodeHash);
        }
        
        [Test]
        public void just_before_istanbul_balance_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .Op(Instruction.BALANCE)
                .Done;

            var result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.BalanceEip150);
        }
        
        [Test]
        public void just_before_istanbul_sload_cost_is_increased()
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether());

            byte[] code = Prepare.EvmCode
                .PushData(TestItem.AddressC)
                .PushData(0)
                .Op(Instruction.SLOAD)
                .Done;

            var result = Execute(BlockNumber - 1, 100000, code);
            AssertGas(result, 21000 + 2 * GasCostOf.VeryLow + GasCostOf.SLoadEip150);
        }
    }
}
