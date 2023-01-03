// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class VmCodeDepositTests : VirtualMachineTestsBase
    {
        private long _blockNumber = MainnetSpecProvider.ByzantiumBlockNumber;

        protected override long BlockNumber => _blockNumber;

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

            WorldState.CreateAccount(TestItem.AddressC, 1.Ether());
            WorldState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            byte[] result = WorldState.Get(storageCell);
            Assert.AreEqual(new byte[] { 0 }, result, "storage reverted");
            Assert.AreEqual(98777, receipt.GasSpent, "no refund");

            byte[] returnData = WorldState.Get(new StorageCell(TestItem.AddressC, 0));
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

            WorldState.CreateAccount(TestItem.AddressC, 1.Ether());
            WorldState.InsertCode(TestItem.AddressC, createCode, Spec);

            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 32000 + 20003 + 20000 + 5000 + 500 + 0) // not enough
                .Done;

            TestAllTracerWithOutput receipt = Execute(code);
            byte[] result = WorldState.Get(storageCell);
            Assert.AreEqual(new byte[] { 0 }, result, "storage reverted");
            Assert.AreEqual(83199, receipt.GasSpent, "with refund");

            byte[] returnData = WorldState.Get(new StorageCell(TestItem.AddressC, 0));
            Assert.AreEqual(deployed.Bytes, returnData, "address returned");
        }
    }
}
