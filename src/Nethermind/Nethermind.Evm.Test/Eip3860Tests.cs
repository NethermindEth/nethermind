//  Copyright (c) 2022 Demerzel Solutions Limited
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

using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using Nethermind.Specs.Test;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Test
{
    public class Eip3860Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.ShanghaiBlockNumber;
        protected bool isEIP3860Enabled;

        [TestCase("0x61013860006000f0", false, 32039)] //length 312
        [TestCase("0x61013860006000f0", true, 32059)] //extra 20 cost
        [TestCase("0x600261013860006000f5", false, 32102)] //length 312
        [TestCase("0x600261013860006000f5", true, 32122)] //extra 20 cost
        //cases from geth implementation
        [TestCase("0x61C00060006000f0", false, 41225)]
        [TestCase("0x61C00060006000f0", true, 44297)]
        [TestCase("0x600061C00060006000f5", false, 50444)]
        [TestCase("0x600061C00060006000f5", true, 53516)]
        public void Test_EIP_3860_GasCost_Create(string createCode, bool eip3860Enabled, long expectedGasUsage)
        {
            isEIP3860Enabled = eip3860Enabled;

            byte[] byteCode = Prepare.EvmCode
                .FromCode(createCode)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(byteCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] callCode = Prepare.EvmCode.Call(TestItem.AddressC, 100000).Done;

            var tracer = Execute(callCode);
            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
            Assert.AreEqual(expectedGasUsage, tracer.GasSpent - (GasCostOf.Transaction + 100 + 7 * GasCostOf.VeryLow));
        }

        [Test]
        public void Test_EIP_3860_InitCode_CREATE_Exceeds_Limit()
        {
            isEIP3860Enabled = true;
            string dataLenghtHex = (Spec.MaxInitCodeSize + 1).ToString("X");
            var dataPush = Instruction.PUSH1 + (byte)(dataLenghtHex.Length / 2 - 1);

            byte[] byteCode = Prepare.EvmCode
                .FromCode(dataPush.ToString("X") + dataLenghtHex + "60006000f0")
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());
            Keccak createCodeHash = TestState.UpdateCode(byteCode);
            TestState.UpdateCodeHash(TestItem.AddressC, createCodeHash, Spec);

            byte[] callCode = Prepare.EvmCode.Call(TestItem.AddressC, 50000).Done;

            var tracer = Execute(callCode);
            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
            //how to test byteCode returned empty (CREATE) not call ?
            //Assert.AreEqual(StatusCode.FailureBytes, tracer.ReturnValue);
            Assert.AreEqual(Array.Empty<byte>(), tracer.ReturnValue);
            //init code gas cost not deducted, but cost of 3 * push deducted
            Assert.AreEqual(0, tracer.GasSpent - (GasCostOf.Transaction + 100 + 7 * GasCostOf.VeryLow + 3 * GasCostOf.VeryLow));
        }

        [Test]
        public void Test_EIP_3860_Disabled_InitCode_TxCreation_Exceeds_Limit_Succeeds()
        {
            var tracer = PrepExecuteCreateTransaction(Spec.MaxInitCodeSize + 1);

            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
        }

        [Test]
        public void Test_EIP_3860_Enabled_InitCode_TxCreation_Exceeds_Limit_Fails()
        {
            isEIP3860Enabled = true;

            var tracer = PrepExecuteCreateTransaction(Spec.MaxInitCodeSize + 1);

            Assert.AreEqual(StatusCode.Failure, tracer.StatusCode);
            Assert.AreEqual(tracer.Error, "eip-3860 - transaction size over max init code size");
        }

        [Test]
        public void Test_EIP_3860_Enabled_InitCode_TxCreation_Within_Limit_Succeeds()
        {
            isEIP3860Enabled = true;

            //7680 is the size of create instructions - Prepare.EvmCode.Create
            var tracer = PrepExecuteCreateTransaction(Spec.MaxInitCodeSize - 7680);

            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
        }

        protected TestAllTracerWithOutput PrepExecuteCreateTransaction(long byteCodeSize)
        {
            var byteCode = new byte[byteCodeSize];

            byte[] createCode = Prepare.EvmCode.Create(byteCode, 0).Done;

            TestState.CreateAccount(TestItem.AddressC, 1.Ether());

            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 500000, createCode);

            transaction.GasPrice = 2.GWei();
            transaction.To = null;
            transaction.Data = createCode;
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            return tracer;
        }

        protected override ISpecProvider SpecProvider => new OverridableSpecProvider(new TestSpecProvider(Shanghai.Instance), r => new OverridableReleaseSpec(r) { IsEip3860Enabled = isEIP3860Enabled});
    }
}
