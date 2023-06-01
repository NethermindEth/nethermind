// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.Self)]
    public class VirtualMachineTests : VirtualMachineTestsBase
    {
        [Test]
        public void Stop()
        {
            TestAllTracerWithOutput receipt = Execute((byte)Instruction.STOP);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction));
        }

        [Test]
        public void Trace()
        {
            GethLikeTxTrace trace = ExecuteAndTrace(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);

            Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
            GethTxTraceEntry entry = trace.Entries[1];
            Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
            Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
            Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
            Assert.That(entry.Memory.Count, Is.EqualTo(0), nameof(entry.Memory));
            Assert.That(entry.Stack.Count, Is.EqualTo(1), nameof(entry.Stack));
            Assert.That(trace.Entries[4].Storage.Count, Is.EqualTo(1), nameof(entry.Storage));
            Assert.That(entry.Pc, Is.EqualTo(2), nameof(entry.Pc));
            Assert.That(entry.Operation, Is.EqualTo("PUSH1"), nameof(entry.Operation));
        }

        [Test]
        public void Trace_vm_errors()
        {
            GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);

            Assert.True(trace.Entries.Any(e => e.Error is not null));
        }

        [Test]
        public void Trace_memory_out_of_gas_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData((UInt256)(10 * 1000 * 1000))
                .Op(Instruction.MLOAD)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

            Assert.True(trace.Entries.Any(e => e.Error is not null));
        }

        [Test]
        [Ignore("// https://github.com/NethermindEth/nethermind/issues/140")]
        public void Trace_invalid_jump_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData(255)
                .Op(Instruction.JUMP)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

            Assert.True(trace.Entries.Any(e => e.Error is not null));
        }

        [Test]
        [Ignore("// https://github.com/NethermindEth/nethermind/issues/140")]
        public void Trace_invalid_jumpi_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData(1)
                .PushData(255)
                .Op(Instruction.JUMPI)
                .Done;

            GethLikeTxTrace trace = ExecuteAndTrace(1L, 21000L + 19000L, code);

            Assert.True(trace.Entries.Any(e => e.Error is not null));
        }

        [Test(Description = "Test a case where the trace is created for one transaction and subsequent untraced transactions keep adding entries to the first trace created.")]
        public void Trace_each_tx_separate()
        {
            GethLikeTxTrace trace = ExecuteAndTrace(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);

            Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);

            Assert.That(trace.Entries.Count, Is.EqualTo(5), "number of entries");
            GethTxTraceEntry entry = trace.Entries[1];
            Assert.That(entry.Depth, Is.EqualTo(1), nameof(entry.Depth));
            Assert.That(entry.Gas, Is.EqualTo(79000 - GasCostOf.VeryLow), nameof(entry.Gas));
            Assert.That(entry.GasCost, Is.EqualTo(GasCostOf.VeryLow), nameof(entry.GasCost));
            Assert.That(entry.Memory.Count, Is.EqualTo(0), nameof(entry.Memory));
            Assert.That(entry.Stack.Count, Is.EqualTo(1), nameof(entry.Stack));
            Assert.That(trace.Entries[4].Storage.Count, Is.EqualTo(1), nameof(entry.Storage));
            Assert.That(entry.Pc, Is.EqualTo(2), nameof(entry.Pc));
            Assert.That(entry.Operation, Is.EqualTo("PUSH1"), nameof(entry.Operation));
        }

        [Test]
        public void Add_0_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(new byte[] { 0 }), "storage");
        }

        [Test]
        public void Add_0_1()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(new byte[] { 1 }), "storage");
        }

        [Test]
        public void Add_1_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(new byte[] { 1 }), "storage");
        }

        [Test]
        public void Mstore()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                96, // data
                (byte)Instruction.PUSH1,
                64, // position
                (byte)Instruction.MSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Memory * 3), "gas");
        }

        [Test]
        public void Mstore_twice_same_location()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                96,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MSTORE,
                (byte)Instruction.PUSH1,
                96,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 6 + GasCostOf.Memory * 3), "gas");
        }

        [Test]
        public void Mload()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                64, // position
                (byte)Instruction.MLOAD);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.Memory * 3), "gas");
        }

        [Test]
        public void Mload_after_mstore()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                96,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MSTORE,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MLOAD);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory * 3), "gas");
        }

        [Test]
        public void Dup1()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.DUP1);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2), "gas");
        }

        [Test]
        public void Codecopy()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                32, // length
                (byte)Instruction.PUSH1,
                0, // src
                (byte)Instruction.PUSH1,
                32, // dest
                (byte)Instruction.CODECOPY);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3), "gas");
        }

        [Test]
        public void Swap()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                32, // length
                (byte)Instruction.PUSH1,
                0, // src
                (byte)Instruction.SWAP1);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3), "gas");
        }

        [Test]
        public void Sload()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0, // index
                (byte)Instruction.SLOAD);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.SLoadEip150), "gas");
        }

        [Test]
        public void Exp_2_160()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                2,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.Pow(2, 160).ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Exp_0_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Exp_0_160()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Exp_1_160()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Sub_0_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(new byte[] { 0 }), "storage");
        }

        [Test]
        public void Not_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.NOT,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Or_0_0()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.OR,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }

        [Test]
        public void Sstore_twice_0_same_storage_should_refund_only_once()
        {
            TestAllTracerWithOutput receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset), "gas");
            Assert.That(TestState.Get(new StorageCell(Recipient, 0)), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
        }

        /// <summary>
        /// TLoad gas cost check
        /// </summary>
        [Test]
        public void Tload()
        {
            byte[] code = Prepare.EvmCode
                .PushData(96)
                .Op(Instruction.TLOAD)
                .Done;

            TestAllTracerWithOutput receipt = Execute(MainnetSpecProvider.GrayGlacierBlockNumber, 100000, code, timestamp: MainnetSpecProvider.CancunBlockTimestamp);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.TLoad), "gas");
        }

        /// <summary>
        /// TStore gas cost check
        /// </summary>
        [Test]
        public void Tstore()
        {
            byte[] code = Prepare.EvmCode
                .PushData(96)
                .PushData(64)
                .Op(Instruction.TSTORE)
                .Done;

            TestAllTracerWithOutput receipt = Execute(MainnetSpecProvider.GrayGlacierBlockNumber, 100000, code, timestamp: MainnetSpecProvider.CancunBlockTimestamp);
            Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.TStore), "gas");
        }

        [Test]
        [Ignore("Not yet implemented")]
        public void Ropsten_attack_contract_test()
        {
            //PUSH1 0x60
            //PUSH1 0x40
            //MSTORE
            //PUSH4 0xffffffff
            //PUSH1 0xe0
            //PUSH1 0x02
            //EXP
            //PUSH1 0x00
            //CALLDATALOAD
            //DIV
            //AND
            //PUSH4 0x9fe12a6a
            //DUP2
            //EQ
            //PUSH1 0x22
            //JUMPI
            //JUMPDEST
            //PUSH1 0x00
            //JUMP
            //JUMPDEST
            //CALLVALUE
            //PUSH1 0x00
            //JUMPI
            //PUSH1 0x38
            //PUSH1 0x04
            //CALLDATALOAD
            //PUSH1 0x24
            //CALLDATALOAD
            //PUSH1 0xff
            //PUSH1 0x44
            //CALLDATALOAD
            //AND
            //PUSH1 0x3a
            //JUMP
            //JUMPDEST
            //STOP
            //JUMPDEST
            //PUSH1 0x40
            //DUP1
            //MLOAD
            //PUSH1 0xff
            //DUP4
            //AND
            //DUP2
            //MSTORE
            //SWAP1
            //MLOAD
            //DUP4
            //SWAP2
            //DUP6
            //SWAP2
            //PUSH32 0x2f554056349a3530a4cabe3891d711b94a109411500421e48fc5256d660d7a79
            //SWAP2
            //DUP2
            //SWAP1
            //SUB
            //PUSH1 0x20
            //ADD
            //SWAP1
            //LOG3
            //JUMPDEST
            //POP
            //POP
            //POP
            //JUMP
            //STOP
        }
    }
}
