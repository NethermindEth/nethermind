// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class GethLikeTxMemoryTracerTests : VirtualMachineTestsBase
{
    [Test]
    public void Can_trace_gas_halt_with_stop()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x1")
            .PushData("0x2")
            .Op(Instruction.ADD)
            .Op(Instruction.STOP)
            .Done;

        int[] gasCosts = new int[] { 3, 3, 3, 0 };

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        int gasTotal = 0;
        for (int i = 0; i < gasCosts.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(trace.Entries[i].Gas, Is.EqualTo(79000 - gasTotal), $"gas[{i}]");
                Assert.That(trace.Entries[i].GasCost, Is.EqualTo(gasCosts[i]), $"gasCost[{i}]");
            }
            gasTotal += gasCosts[i];
        }
    }

    [Test]
    public void Can_trace_gas_halt_with_return()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x1")
            .PushData("0x2")
            .Op(Instruction.ADD)
            .Op(Instruction.RETURN)
            .Done;

        int[] gasCosts = new int[] { 3, 3, 3, 0 };

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        int gasTotal = 0;
        for (int i = 0; i < gasCosts.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(trace.Entries[i].Gas, Is.EqualTo(79000 - gasTotal), $"gas[{i}]");
                Assert.That(trace.Entries[i].GasCost, Is.EqualTo(gasCosts[i]), $"gasCost[{i}]");
            }
            gasTotal += gasCosts[i];
        }
    }

    [Test]
    [Todo("Verify the exact error string in Geth")]
    public void Can_trace_stack_underflow_failure()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.ADD)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Failed, Is.EqualTo(true));
            Assert.That(trace.Entries[0].Error, Is.EqualTo("StackUnderflow"));
        }
    }

    [Test]
    [Todo("Verify the exact error string in Geth")]
    public void Can_trace_stack_overflow_failure()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData("0xab")
            .PushData("0x0")
            .Op(Instruction.JUMP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Failed, Is.EqualTo(true));
            Assert.That(trace.Entries.Last().Error, Is.EqualTo("StackOverflow"));
        }
    }

    [Test]
    [Todo("Verify the exact error string in Geth")]
    public void Can_trace_invalid_jump_failure()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0xab")
            .Op(Instruction.JUMP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Failed, Is.EqualTo(true));
            Assert.That(trace.Entries.Last().Error, Is.EqualTo("BadJumpDestination"));
        }
    }

    [Test]
    [Todo("Verify the exact error string in Geth")]
    public void Can_trace_invalid_opcode_failure()
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.INVALID)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Failed, Is.EqualTo(true));
            Assert.That(trace.Entries.Last().Error, Is.EqualTo("BadInstruction"));
        }
    }

    [Test]
    public void Can_trace_opcodes()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0xa01234")
            .PushData("0x0")
            .Op(Instruction.STOP)
            .Done;

        string[] opCodes = new[] { "PUSH3", "PUSH1", "STOP" };

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        for (int i = 0; i < opCodes.Length; i++)
        {
            Assert.That(trace.Entries[i].Opcode, Is.EqualTo(opCodes[i]));
        }
    }

    [Test]
    public void Can_trace_call_depth()
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        byte[] createCode = Prepare.EvmCode
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        int[] depths = new int[]
        {
            1, 1, 1, 1, 1, 1, 1, 1, // STACK FOR CALL
              2, 2, 2, 2, 2, 2, 2, 2, 2, 2, // CALL
                3, 3, 3, 3, 3, 3, // CREATE
              2, // STOP
            1, // STOP
        };

        Assert.That(trace.Entries.Count, Is.EqualTo(depths.Length));
        for (int i = 0; i < depths.Length; i++)
        {
            Assert.That(trace.Entries[i].Depth, Is.EqualTo(depths[i]), $"entries[{i}]");
        }
    }

    [Test]
    public void Stack_is_cleared_and_restored_when_moving_between_call_levels()
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        byte[] createCode = Prepare.EvmCode
            .PushData(SampleHexData1) // just to test if stack is restored
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1) // just to test if stack is restored
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        /* depths
        {
            1, 1, 1, 1, 1, 1, 1, 1, 1, // SAMPLE STACK + STACK FOR CALL [0..8]
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // SAMPLE STACK + CALL [9..19]
            3, 3, 3, 3, 3, 3, // CREATE [20..25]
            2, // STOP [26]
            1, // STOP [27]
        }; */

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries[0].StackWordCount(), Is.EqualTo(0), "BEGIN 1");
            Assert.That(trace.Entries[8].StackWordCount(), Is.EqualTo(8), "CALL FROM 1");
            Assert.That(trace.Entries[9].StackWordCount(), Is.EqualTo(0), "BEGIN 2");
            Assert.That(trace.Entries[19].StackWordCount(), Is.EqualTo(4), "CREATE FROM 2");
            Assert.That(trace.Entries[20].StackWordCount(), Is.EqualTo(0), "BEGIN 3");
            Assert.That(trace.Entries[25].StackWordCount(), Is.EqualTo(2), "END 3");
            Assert.That(trace.Entries[26].StackWordCount(), Is.EqualTo(2), "END 2");
            Assert.That(trace.Entries[27].StackWordCount(), Is.EqualTo(2), "END 1");
        }
    }

    [Test]
    public void Memory_is_cleared_and_restored_when_moving_between_call_levels()
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        byte[] createCode = Prepare.EvmCode
            .StoreDataInMemory(32, SampleHexData1.PadLeft(64, '0')) // just to test if memory is restored
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        byte[] code = Prepare.EvmCode
            .StoreDataInMemory(64, SampleHexData2.PadLeft(64, '0')) // just to test if memory is restored
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        /* depths
        {
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 // MEMORY + STACK FOR CALL [0..10]
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // MEMORY + CALL [11..23]
            3, 3, 3, 3, 3, 3, // CREATE [24..29]
            2, // STOP [30]
            1, // STOP [21]
        }; */

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries[0].MemoryWordCount(), Is.EqualTo(0), "BEGIN 1");
            Assert.That(trace.Entries[10].MemoryWordCount(), Is.EqualTo(3), "CALL FROM 1");
            Assert.That(trace.Entries[11].MemoryWordCount(), Is.EqualTo(0), "BEGIN 2");
            Assert.That(trace.Entries[23].MemoryWordCount(), Is.EqualTo(2), "CREATE FROM 2");
            Assert.That(trace.Entries[24].MemoryWordCount(), Is.EqualTo(0), "BEGIN 3");
            Assert.That(trace.Entries[29].MemoryWordCount(), Is.EqualTo(1), "END 3");
            Assert.That(trace.Entries[30].MemoryWordCount(), Is.EqualTo(2), "END 2");
            Assert.That(trace.Entries[31].MemoryWordCount(), Is.EqualTo(3), "END 1");
        }
    }

    [Test]
    public void Storage_is_cleared_and_restored_when_moving_between_call_levels()
    {
        byte[] deployedCode = new byte[3];

        byte[] initCode = Prepare.EvmCode
            .ForInitOf(deployedCode)
            .Done;

        byte[] createCode = Prepare.EvmCode
            .PersistData("0x1", HexZero) // just to test if storage is restored
            .Create(initCode, 0)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, createCode, Spec);

        byte[] code = Prepare.EvmCode
            .PersistData("0x2", HexZero) // just to test if storage is restored
            .PersistData("0x3", HexZero) // just to test if storage is restored
            .Call(TestItem.AddressC, 70000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        /* depths
        {
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 // 2x SSTORE + STACK FOR CALL [0..13]
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 // SSTORE + CALL [14..26]
            3, 3, 3, 3, 3, 3, // CREATE [27..32]
            2, // STOP [33]
            1, // STOP [34]
        }; */

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.Entries[0].Storage, Is.Null, "BEGIN 1");
            Assert.That(trace.Entries[13].Storage, Is.Null, "CALL FROM 1");
            Assert.That(trace.Entries[14].Storage, Is.Null, "BEGIN 2");
            Assert.That(trace.Entries[26].Storage, Is.Null, "CREATE FROM 2");
            Assert.That(trace.Entries[27].Storage, Is.Null, "BEGIN 3");
            Assert.That(trace.Entries[32].Storage, Is.Null, "END 3");
            Assert.That(trace.Entries[33].Storage, Is.Null, "END 2");
            Assert.That(trace.Entries[34].Storage, Is.Null, "END 1");

            Assert.That(trace.Entries[2].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x2 opcode");
            Assert.That(trace.Entries[5].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x3 opcode");
            Assert.That(trace.Entries[16].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x1 opcode");
        }

        // Storage content is built lazily during serialization; verify via JSON.
        using JsonDocument doc = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(trace));
        JsonElement[] logs = doc.RootElement.GetProperty("structLogs").EnumerateArray().ToArray();
        const string slot1 = "0x0000000000000000000000000000000000000000000000000000000000000001";
        const string slot2 = "0x0000000000000000000000000000000000000000000000000000000000000002";
        const string slot3 = "0x0000000000000000000000000000000000000000000000000000000000000003";
        const string zero = "0x0000000000000000000000000000000000000000000000000000000000000000";

        using (Assert.EnterMultipleScope())
        {
            JsonElement s2 = logs[2].GetProperty("storage");
            Assert.That(s2.EnumerateObject().Count(), Is.EqualTo(1), "SSTORE 0x2: one slot");
            Assert.That(s2.GetProperty(slot2).GetString(), Is.EqualTo(zero), "SSTORE 0x2: slot2=0");

            JsonElement s5 = logs[5].GetProperty("storage");
            Assert.That(s5.EnumerateObject().Count(), Is.EqualTo(2), "SSTORE 0x3: two slots");
            Assert.That(s5.GetProperty(slot2).GetString(), Is.EqualTo(zero), "SSTORE 0x3: slot2 still present");
            Assert.That(s5.GetProperty(slot3).GetString(), Is.EqualTo(zero), "SSTORE 0x3: slot3=0");

            JsonElement s16 = logs[16].GetProperty("storage");
            Assert.That(s16.EnumerateObject().Count(), Is.EqualTo(1), "SSTORE 0x1: isolated to callee");
            Assert.That(s16.GetProperty(slot1).GetString(), Is.EqualTo(zero), "SSTORE 0x1: slot1=0");
        }
    }

    [Test]
    public void Can_trace_pc()
    {
        byte[] code = Prepare.EvmCode
            .PushData("0x0") // 0
            .Op(Instruction.JUMPDEST) // 2
            .PushData("0x1") // 3
            .Op(Instruction.ADD) // 5
            .Op(Instruction.DUP1) // 6
            .PushData("0x3") // 7
            .Op(Instruction.GT) // 9
            .PushData("0x2") // 10
            .Op(Instruction.JUMPI) // 12
            .Op(Instruction.STOP) // 13
            .Done;

        int[] pcs = new[] { 0, 2, 3, 5, 6, 7, 9, 10, 12, 2, 3, 5, 6, 7, 9, 10, 12, 2, 3, 5, 6, 7, 9, 10, 12, 13 };

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        Assert.That(trace.Entries.Count, Is.EqualTo(pcs.Length));
        for (int i = 0; i < pcs.Length; i++)
        {
            Assert.That(trace.Entries[i].ProgramCounter, Is.EqualTo(pcs[i]));
        }
    }

    [Test]
    public void Can_trace_stack()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1)
            .PushData(HexZero)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        UInt256 sample1 = Hex(SampleHexData1);

        Assert.That(trace.Entries[0].StackWordCount(), Is.EqualTo(0), "entry[0] length");

        Assert.That(trace.Entries[1].StackWordCount(), Is.EqualTo(1), "entry[1] length");
        Assert.That(trace.Entries[1].GetStackWord(0), Is.EqualTo(sample1), "entry[1][0]");

        Assert.That(trace.Entries[2].StackWordCount(), Is.EqualTo(2), "entry[2] length");
        Assert.That(trace.Entries[2].GetStackWord(0), Is.EqualTo(sample1), "entry[2][0]");
        Assert.That(trace.Entries[2].GetStackWord(1), Is.EqualTo(UInt256.Zero), "entry[2][1]");

        Assert.That(trace.Entries[3].StackWordCount(), Is.EqualTo(1), "entry[3] length");
        Assert.That(trace.Entries[3].GetStackWord(0), Is.EqualTo(sample1), "entry[3][0]");
    }

    [Test]
    public void Can_trace_memory()
    {
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(SampleHexData2.PadLeft(64, '0'))
            .PushData(32)
            .Op(Instruction.MSTORE)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        /* note the curious Geth trace behaviour where memory grows now but is populated from the next trace entry */

        UInt256 sample1 = Hex(SampleHexData1);

        Assert.That(trace.Entries[0].MemoryWordCount(), Is.EqualTo(0), "entry[0] length");

        Assert.That(trace.Entries[1].MemoryWordCount(), Is.EqualTo(0), "entry[1] length");

        Assert.That(trace.Entries[2].MemoryWordCount(), Is.EqualTo(0), "entry[2] length");

        Assert.That(trace.Entries[3].MemoryWordCount(), Is.EqualTo(1), "entry[3] length");
        Assert.That(trace.Entries[3].GetMemoryWord(0), Is.EqualTo(sample1), "entry[3][0]");

        Assert.That(trace.Entries[4].MemoryWordCount(), Is.EqualTo(1), "entry[4] length");
        Assert.That(trace.Entries[4].GetMemoryWord(0), Is.EqualTo(sample1), "entry[4][0]");

        Assert.That(trace.Entries[5].MemoryWordCount(), Is.EqualTo(1), "entry[5] length");
        Assert.That(trace.Entries[5].GetMemoryWord(0), Is.EqualTo(sample1), "entry[5][0]");
    }

    [Test]
    public void Can_trace_extcodesize_optimization()
    {
        // From https://github.com/NethermindEth/nethermind/issues/5717
        byte[] code = Bytes.FromHexString("0x60246044607460d1606b60b9603369866833515b6d086c607f3b15749e4886579008320052006f");

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        AssertEntry(trace.Entries[^3], expectedPc: 25, expectedOpcode: "EXTCODESIZE", expectedStackTop: Hex("866833515b6d086c607f"), expectedStackCount: 8);
        AssertEntry(trace.Entries[^2], expectedPc: 26, expectedOpcode: "ISZERO", expectedStackTop: UInt256.Zero, expectedStackCount: 8);
        AssertEntry(trace.Entries[^1], expectedPc: 27, expectedOpcode: "PUSH21", expectedStackTop: UInt256.One, expectedStackCount: 8);
    }

    [Test]
    public void Can_trace_refund_on_storage_clear()
    {
        // Seed a non-zero slot so clearing it to zero grants a storage-clearing refund.
        TestState.CreateAccount(Recipient, 1.Ether);
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        GethTxTraceEntry sstore = trace.Entries.Single(e => e.Opcode == "SSTORE");
        GethTxTraceEntry stop = trace.Entries.Single(e => e.Opcode == "STOP");

        using (Assert.EnterMultipleScope())
        {
            // The counter is captured before the opcode runs, so SSTORE itself shows no refund yet.
            Assert.That(sstore.Refund, Is.Null, "refund before SSTORE executes");
            Assert.That(stop.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund), "refund after the clearing SSTORE");
        }
    }

    [Test]
    public void Refund_is_rolled_back_when_frame_reverts()
    {
        byte[] calleeCode = Prepare.EvmCode
            .PersistData("0x0", HexZero)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.REVERT)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.Set(new StorageCell(TestItem.AddressC, 0), new byte[] { 1 });
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        GethTxTraceEntry revert = trace.Entries.Single(e => e.Opcode == "REVERT");
        GethTxTraceEntry topLevelStop = trace.Entries.Last(e => e.Opcode == "STOP" && e.Depth == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(revert.Refund, Is.EqualTo(Spec.GasCosts.SClearRefund), "refund visible inside the reverting frame");
            Assert.That(topLevelStop.Refund, Is.Null, "refund rolled back after the frame reverts");
        }
    }

    [Test]
    public void Can_trace_returndata_when_enabled()
    {
        const string word = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        byte[] calleeCode = Prepare.EvmCode
            .StoreDataInMemory(0, word)
            .Return(32, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(GethTraceOptions.Default with { EnableReturnData = true }, code);

        GethTxTraceEntry call = trace.Entries.Last(e => e.Opcode == "CALL");
        GethTxTraceEntry topLevelStop = trace.Entries.Last(e => e.Opcode == "STOP" && e.Depth == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.ReturnData, Is.Null, "no return data before the call executes");
            Assert.That(topLevelStop.ReturnData, Is.EqualTo($"0x{word}"), "return data visible after the inner call returns");
        }
    }

    [Test]
    public void ReturnData_is_omitted_when_not_enabled()
    {
        const string word = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        byte[] calleeCode = Prepare.EvmCode
            .StoreDataInMemory(0, word)
            .Return(32, 0)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);
        TestState.Commit(Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 50000)
            .Op(Instruction.STOP)
            .Done;

        // Default options leave EnableReturnData off, so the field must never be populated.
        GethLikeTxTrace trace = ExecuteAndTrace(code);

        Assert.That(trace.Entries.All(e => e.ReturnData is null), Is.True);
    }

    [Test]
    public void Storage_snapshot_accumulates_per_address_across_repeated_calls_to_same_contract()
    {
        const string val1 = "11";
        const string val2 = "22";

        byte[] calleeCode = Prepare.EvmCode
            .PersistData("0x1", val1)
            .PersistData("0x2", val2)
            .Op(Instruction.STOP)
            .Done;

        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, calleeCode, Spec);

        byte[] code = Prepare.EvmCode
            .Call(TestItem.AddressC, 70000)
            .Call(TestItem.AddressC, 70000)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        Assert.That(trace.Entries.Count(e => e.Opcode == "SSTORE"), Is.EqualTo(4),
            "expected four SSTORE entries (two per invocation)");

        AssertStreamingMatchesInMemory(code);
    }

    [Test]
    public void Storage_snapshot_is_emitted_on_SLOAD()
    {
        const string val = "42";

        byte[] code = Prepare.EvmCode
            .PersistData("0x1", val)
            .PushData("0x1")
            .Op(Instruction.SLOAD)
            .Op(Instruction.STOP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        Assert.That(trace.Entries.Any(e => e.Opcode == "SLOAD"), Is.True, "expected an SLOAD entry");

        AssertStreamingMatchesInMemory(code);
    }

    private void AssertStreamingMatchesInMemory(byte[] code)
    {
        GethLikeTxTrace trace = ExecuteAndTrace(code);
        using JsonDocument inMemoryDoc = JsonDocument.Parse(new EthereumJsonSerializer().Serialize(trace));
        JsonElement[] inMemory = inMemoryDoc.RootElement.GetProperty("structLogs").EnumerateArray().ToArray();
        JsonElement[] streamed = ExecuteStreamingTracerEntries(code);

        Assert.That(inMemory.Length, Is.EqualTo(streamed.Length), "entry count");
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < inMemory.Length; i++)
                Assert.That(JsonElement.DeepEquals(inMemory[i], streamed[i]), Is.True,
                    $"entry[{i}] differs\n in-memory: {inMemory[i].GetRawText()}\n streaming: {streamed[i].GetRawText()}");
        }
    }

    private JsonElement[] ExecuteStreamingTracerEntries(byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            GethLikeTxDirectStreamingTracer tracer = new(transaction, GethTraceOptions.Default with { EnableMemory = true }, writer, pipeWriter: null, CancellationToken.None);
            writer.WriteStartArray();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            tracer.BuildResult();
            writer.WriteEndArray();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static void AssertEntry(GethTxTraceEntry entry, long expectedPc, string expectedOpcode, UInt256 expectedStackTop, int expectedStackCount)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ProgramCounter, Is.EqualTo(expectedPc));
            Assert.That(entry.Opcode, Is.EqualTo(expectedOpcode));
            Assert.That(entry.GetStackWord(entry.StackWordCount() - 1), Is.EqualTo(expectedStackTop));
            Assert.That(entry.StackWordCount(), Is.EqualTo(expectedStackCount));
        }
    }

    private static UInt256 Hex(string hex) => new(Bytes.FromHexString(hex), isBigEndian: true);
}
