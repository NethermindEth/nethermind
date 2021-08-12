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
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.Self)]
    public class VmCodeDepositTests : VirtualMachineTestsBase
    {
        private long _blockNumber = MainnetSpecProvider.ByzantiumBlockNumber;

        protected override long BlockNumber => _blockNumber;

        public VmCodeDepositTests(bool useBeamSync)
        {
            UseBeamSync = useBeamSync;
        }
        
        [SetUp]
        public override void Setup()
        {
            base.Setup();
            _blockNumber = MainnetSpecProvider.ByzantiumBlockNumber;
        }
        
        [Test(Description = "Refunds should not be given when the call fails due to lack of gas for code deposit payment")]
        public void Regression_mainnet_6108276()
        {
            Address deployed = ContractAddress.From(TestItem.AddressC, 0);
            StorageCell storageCell = new(deployed, 1);

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

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            var receipt = Execute(code);
            byte[] result = Storage.Get(storageCell);
            Assert.AreEqual(new byte[] {0}, result, "storage reverted");
            Assert.AreEqual(98777, receipt.GasSpent, "no refund");
            
            byte[] returnData = Storage.Get(new StorageCell(TestItem.AddressC, 0));
            Assert.AreEqual(new byte[1], returnData, "address returned");
        }
        
        [Test(Description = "Deposit OutOfGas before EIP-2")]
        public void Regression_mainnet_226522()
        {
            _blockNumber = 1;
            Address deployed = ContractAddress.From(TestItem.AddressC, 0);
            StorageCell storageCell = new(deployed, 1);

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

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(createCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            var receipt = Execute(code);
            byte[] result = Storage.Get(storageCell);
            Assert.AreEqual(new byte[] {0}, result, "storage reverted");
            Assert.AreEqual(83199, receipt.GasSpent, "with refund");
            
            byte[] returnData = Storage.Get(new StorageCell(TestItem.AddressC, 0));
            Assert.AreEqual(deployed.Bytes, returnData, "address returned");
        }
    }
}
