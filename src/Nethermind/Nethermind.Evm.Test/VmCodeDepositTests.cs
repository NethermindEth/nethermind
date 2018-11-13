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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class VmCodeDepositTests : VirtualMachineTestsBase
    {
        private UInt256 _blockNumber = MainNetSpecProvider.ByzantiumBlockNumber;

        protected override UInt256 BlockNumber => _blockNumber;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _blockNumber = MainNetSpecProvider.ByzantiumBlockNumber;
        }
        
        [Test(Description = "Refunds should not be given when the call fails due to lack of gas for code deposit payment")]
        public void Regression_mainnet_6108276()
        {
            Address deployed = Address.OfContract(TestObject.AddressC, 0);
            StorageAddress storageAddress = new StorageAddress(deployed, 1);

            byte[] deployedCode = new byte[100]; // cost is * 200

            byte[] initCode = Prepare.EvmCode
                .PushData(1)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(1)
                .Op(Instruction.SSTORE) // here we reset storage so we would get refund of 15000 gas
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestObject.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            (TransactionReceipt receipt, TransactionTrace trace) = ExecuteAndTrace(code);
            byte[] result = Storage.Get(storageAddress);
            Assert.AreEqual(new byte[] {0}, result, "storage reverted");
            Assert.AreEqual(98777, receipt.GasUsed, "no refund");
            
            byte[] returnData = Storage.Get(new StorageAddress(TestObject.AddressC, 0));
            Assert.AreEqual(new byte[1], returnData, "address returned");
        }
        
        [Test(Description = "Deposit OutOfGas before EIP-2")]
        public void Regression_mainnet_226522()
        {
            _blockNumber = 1;
            Address deployed = Address.OfContract(TestObject.AddressC, 0);
            StorageAddress storageAddress = new StorageAddress(deployed, 1);

            byte[] deployedCode = new byte[106]; // cost is * 200

            byte[] initCode = Prepare.EvmCode
                .PushData(1)
                .PushData(1)
                .Op(Instruction.SSTORE)
                .PushData(0)
                .PushData(1)
                .Op(Instruction.SSTORE) // here we reset storage so we would get refund of 15000 gas
                .ForInitOf(deployedCode).Done;

            byte[] createCode = Prepare.EvmCode
                .Create(initCode, 0)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;

            TestState.CreateAccount(TestObject.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestObject.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestObject.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            (TransactionReceipt receipt, TransactionTrace trace) = ExecuteAndTrace(code);
            byte[] result = Storage.Get(storageAddress);
            Assert.AreEqual(new byte[] {0}, result, "storage reverted");
            Assert.AreEqual(83136, receipt.GasUsed, "with refund");
            
            byte[] returnData = Storage.Get(new StorageAddress(TestObject.AddressC, 0));
            Assert.AreEqual(deployed.Bytes, returnData, "address returned");
        }
    }
}