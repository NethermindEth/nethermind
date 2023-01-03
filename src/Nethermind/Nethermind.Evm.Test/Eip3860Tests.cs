// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;
using Nethermind.Int256;

namespace Nethermind.Evm.Test
{
    public class Eip3860Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.ShanghaiBlockTimestamp;

        private readonly long _transactionCallCost = GasCostOf.Transaction + 100 + 7 * GasCostOf.VeryLow;

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
            byte[] byteCode = Prepare.EvmCode
                .FromCode(createCode)
                .Done;

            WorldState.CreateAccount(TestItem.AddressC, 1.Ether());
            WorldState.InsertCode(TestItem.AddressC, byteCode, Spec);

            byte[] callCode = Prepare.EvmCode.Call(TestItem.AddressC, 100000).Done;

            var tracer = Execute(BlockNumber, eip3860Enabled ? Timestamp : Timestamp - 1, callCode);
            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
            Assert.AreEqual(expectedGasUsage, tracer.GasSpent - _transactionCallCost);
        }

        [TestCase("60006000F0")]
        [TestCase("60006000F5")]
        public void Test_EIP_3860_InitCode_Create_Exceeds_Limit(string createCode)
        {
            string dataLenghtHex = (Spec.MaxInitCodeSize + 1).ToString("X");
            Instruction dataPush = Instruction.PUSH1 + (byte)(dataLenghtHex.Length / 2 - 1);

            bool isCreate2 = createCode[^2..] == Instruction.CREATE2.ToString("X");
            byte[] evmCode = isCreate2
                ? Prepare.EvmCode.PushSingle(0).FromCode(dataPush.ToString("X") + dataLenghtHex + createCode).Done
                : Prepare.EvmCode.FromCode(dataPush.ToString("X") + dataLenghtHex + createCode).Done;

            WorldState.CreateAccount(TestItem.AddressC, 1.Ether());
            WorldState.InsertCode(TestItem.AddressC, evmCode, Spec);

            const int contractCreationGasLimit = 50000;
            byte[] callCode = Prepare.EvmCode.Call(TestItem.AddressC, contractCreationGasLimit).Done;

            var tracer = Execute(callCode);
            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
            Assert.AreEqual(1, tracer.ReportedActionErrors.Count);
            Assert.AreEqual(EvmExceptionType.OutOfGas, tracer.ReportedActionErrors[0]);
            Assert.AreEqual((UInt256)0, WorldState.GetAccount(TestItem.AddressC).Nonce);
            Assert.AreEqual(_transactionCallCost + contractCreationGasLimit, tracer.GasSpent);
        }

        [Test]
        public void Test_EIP_3860_Disabled_InitCode_TxCreation_Exceeds_Limit_Succeeds()
        {
            var tracer = PrepExecuteCreateTransaction(MainnetSpecProvider.ShanghaiBlockTimestamp - 1, Spec.MaxInitCodeSize + 1);

            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
        }

        [Test]
        public void Test_EIP_3860_Enabled_InitCode_TxCreation_Exceeds_Limit_Fails()
        {
            var tracer = PrepExecuteCreateTransaction(MainnetSpecProvider.ShanghaiBlockTimestamp, Spec.MaxInitCodeSize + 1);

            Assert.AreEqual(StatusCode.Failure, tracer.StatusCode);
            Assert.AreEqual(tracer.Error, "EIP-3860 - transaction size over max init code size");
        }

        [Test]
        public void Test_EIP_3860_Enabled_InitCode_TxCreation_Within_Limit_Succeeds()
        {
            //7680 is the size of create instructions - Prepare.EvmCode.Create
            var tracer = PrepExecuteCreateTransaction(MainnetSpecProvider.ShanghaiBlockTimestamp, Spec.MaxInitCodeSize - 7680);

            Assert.AreEqual(StatusCode.Success, tracer.StatusCode);
        }

        protected TestAllTracerWithOutput PrepExecuteCreateTransaction(ulong timestamp, long byteCodeSize)
        {
            var byteCode = new byte[byteCodeSize];

            byte[] createCode = Prepare.EvmCode.Create(byteCode, 0).Done;

            WorldState.CreateAccount(TestItem.AddressC, 1.Ether());

            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 500000, createCode, timestamp: timestamp);

            transaction.GasPrice = 2.GWei();
            transaction.To = null;
            transaction.Data = createCode;
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);

            return tracer;
        }
    }
}
