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

using System.Linq;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.Self)]
    public class VirtualMachineTests : VirtualMachineTestsBase
    {
        public VirtualMachineTests(bool useBeamSync)
        {
            UseBeamSync = useBeamSync;
        }
        
        [Test]
        public void Stop()
        {
            var receipt = Execute((byte)Instruction.STOP);
            Assert.AreEqual(GasCostOf.Transaction, receipt.GasSpent);
        }
        
        [Test]
        public void Trace()
        {
            var trace = ExecuteAndTrace(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            
            Assert.AreEqual(5, trace.Entries.Count, "number of entries");
            GethTxTraceEntry entry = trace.Entries[1];
            Assert.AreEqual(1, entry.Depth, nameof(entry.Depth));
            Assert.AreEqual(79000 - GasCostOf.VeryLow, entry.Gas, nameof(entry.Gas));
            Assert.AreEqual(GasCostOf.VeryLow, entry.GasCost, nameof(entry.GasCost));
            Assert.AreEqual(0, entry.Memory.Count, nameof(entry.Memory));
            Assert.AreEqual(1, entry.Stack.Count, nameof(entry.Stack));
            Assert.AreEqual(1, trace.Entries[4].Storage.Count, nameof(entry.Storage));
            Assert.AreEqual(2, entry.Pc, nameof(entry.Pc));
            Assert.AreEqual("PUSH1", entry.Operation, nameof(entry.Operation));
        }
        
        [Test]
        public void Trace_vm_errors()
        {
            var trace = ExecuteAndTrace(1L, 21000L + 19000L, 
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            
            Assert.True(trace.Entries.Any(e => e.Error != null));
        }
        
        [Test]
        public void Trace_memory_out_of_gas_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData((UInt256)(10 * 1000 * 1000))
                .Op(Instruction.MLOAD)
                .Done;
            
            var trace = ExecuteAndTrace(1L, 21000L + 19000L, code);
            
            Assert.True(trace.Entries.Any(e => e.Error != null));
        }
        
        [Test]
        [Ignore("// https://github.com/NethermindEth/nethermind/issues/140")]
        public void Trace_invalid_jump_exception()
        {
            byte[] code = Prepare.EvmCode
                .PushData(255)
                .Op(Instruction.JUMP)
                .Done;
            
            var trace = ExecuteAndTrace(1L, 21000L + 19000L, code);
            
            Assert.True(trace.Entries.Any(e => e.Error != null));
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
            
            var trace = ExecuteAndTrace(1L, 21000L + 19000L, code);
            
            Assert.True(trace.Entries.Any(e => e.Error != null));
        }
        
        [Test(Description = "Test a case where the trace is created for one transaction and subsequent untraced transactions keep adding entries to the first trace created.")]
        public void Trace_each_tx_separate()
        {            
            var trace = ExecuteAndTrace(
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
            
            Assert.AreEqual(5, trace.Entries.Count, "number of entries");
            GethTxTraceEntry entry = trace.Entries[1];
            Assert.AreEqual(1, entry.Depth, nameof(entry.Depth));
            Assert.AreEqual(79000 - GasCostOf.VeryLow, entry.Gas, nameof(entry.Gas));
            Assert.AreEqual(GasCostOf.VeryLow, entry.GasCost, nameof(entry.GasCost));
            Assert.AreEqual(0, entry.Memory.Count, nameof(entry.Memory));
            Assert.AreEqual(1, entry.Stack.Count, nameof(entry.Stack));
            Assert.AreEqual(1, trace.Entries[4].Storage.Count, nameof(entry.Storage));
            Assert.AreEqual(2, entry.Pc, nameof(entry.Pc));
            Assert.AreEqual("PUSH1", entry.Operation, nameof(entry.Operation));
        }
        
        [Test]
        public void Add_0_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset, receipt.GasSpent, "gas");
            Assert.AreEqual(new byte[] {0}, Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }

        [Test]
        public void Add_0_1()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet, receipt.GasSpent, "gas");
            Assert.AreEqual(new byte[] {1}, Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }

        [Test]
        public void Add_1_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.ADD,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet, receipt.GasSpent, "gas");
            Assert.AreEqual(new byte[] {1}, Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }

        [Test]
        public void Mstore()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                96, // data
                (byte)Instruction.PUSH1,
                64, // position
                (byte)Instruction.MSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Memory * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Mstore_twice_same_location()
        {
            var receipt = Execute(
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
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 6 + GasCostOf.Memory * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Mload()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                64, // position
                (byte)Instruction.MLOAD);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.Memory * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Mload_after_mstore()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                96,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MSTORE,
                (byte)Instruction.PUSH1,
                64,
                (byte)Instruction.MLOAD);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Dup1()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.DUP1);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 2, receipt.GasSpent, "gas");
        }

        [Test]
        public void Codecopy()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                32, // length
                (byte)Instruction.PUSH1,
                0, // src
                (byte)Instruction.PUSH1,
                32, // dest
                (byte)Instruction.CODECOPY);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Swap()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                32, // length
                (byte)Instruction.PUSH1,
                0, // src
                (byte)Instruction.SWAP1);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3, receipt.GasSpent, "gas");
        }

        [Test]
        public void Sload()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0, // index
                (byte)Instruction.SLOAD);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.SLoadEip150, receipt.GasSpent, "gas");
        }

        [Test]
        public void Exp_2_160()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                2,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.Pow(2, 160).ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }

        [Test]
        public void Exp_0_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.One.ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Exp_0_160()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.Zero.ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Exp_1_160()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                160,
                (byte)Instruction.PUSH1,
                1,
                (byte)Instruction.EXP,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.One.ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Sub_0_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SUB,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset, receipt.GasSpent, "gas");
            Assert.AreEqual(new byte[] {0}, Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Not_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.NOT,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet, receipt.GasSpent, "gas");
            Assert.AreEqual((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Or_0_0()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.OR,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.Zero.ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
        }
        
        [Test]
        public void Sstore_twice_0_same_storage_should_refund_only_once()
        {
            var receipt = Execute(
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.PUSH1,
                0,
                (byte)Instruction.SSTORE);
            Assert.AreEqual(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset, receipt.GasSpent, "gas");
            Assert.AreEqual(BigInteger.Zero.ToBigEndianByteArray(), Storage.Get(new StorageCell(Recipient, 0)), "storage");
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
