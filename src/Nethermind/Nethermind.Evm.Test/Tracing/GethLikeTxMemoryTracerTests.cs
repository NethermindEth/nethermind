// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core.Specs;
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
            Assert.That(trace.Entries[0].Stack.Count, Is.EqualTo(0), "BEGIN 1");
            Assert.That(trace.Entries[8].Stack.Count, Is.EqualTo(8), "CALL FROM 1");
            Assert.That(trace.Entries[9].Stack.Count, Is.EqualTo(0), "BEGIN 2");
            Assert.That(trace.Entries[19].Stack.Count, Is.EqualTo(4), "CREATE FROM 2");
            Assert.That(trace.Entries[20].Stack.Count, Is.EqualTo(0), "BEGIN 3");
            Assert.That(trace.Entries[25].Stack.Count, Is.EqualTo(2), "END 3");
            Assert.That(trace.Entries[26].Stack.Count, Is.EqualTo(2), "END 2");
            Assert.That(trace.Entries[27].Stack.Count, Is.EqualTo(2), "END 1");
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
            Assert.That(trace.Entries[0].Memory.Count, Is.EqualTo(0), "BEGIN 1");
            Assert.That(trace.Entries[10].Memory.Count, Is.EqualTo(3), "CALL FROM 1");
            Assert.That(trace.Entries[11].Memory.Count, Is.EqualTo(0), "BEGIN 2");
            Assert.That(trace.Entries[23].Memory.Count, Is.EqualTo(2), "CREATE FROM 2");
            Assert.That(trace.Entries[24].Memory.Count, Is.EqualTo(0), "BEGIN 3");
            Assert.That(trace.Entries[29].Memory.Count, Is.EqualTo(1), "END 3");
            Assert.That(trace.Entries[30].Memory.Count, Is.EqualTo(2), "END 2");
            Assert.That(trace.Entries[31].Memory.Count, Is.EqualTo(3), "END 1");
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

        static string Slot(string index) => "0x" + index.PadLeft(64, '0');
        string ZeroWord = "0x" + HexZero.PadLeft(64, '0');

        using (Assert.EnterMultipleScope())
        {
            // Boundary and non-storage opcodes carry no storage.
            Assert.That(trace.Entries[0].Storage, Is.Null, "BEGIN 1");
            Assert.That(trace.Entries[13].Storage, Is.Null, "CALL FROM 1");
            Assert.That(trace.Entries[14].Storage, Is.Null, "BEGIN 2");
            Assert.That(trace.Entries[26].Storage, Is.Null, "CREATE FROM 2");
            Assert.That(trace.Entries[27].Storage, Is.Null, "BEGIN 3");
            Assert.That(trace.Entries[32].Storage, Is.Null, "END 3");
            Assert.That(trace.Entries[33].Storage, Is.Null, "END 2");
            Assert.That(trace.Entries[34].Storage, Is.Null, "END 1");

            // Depth 1: first SSTORE shows only slot 0x2; second SSTORE shows the cumulative {0x2, 0x3}.
            Assert.That(trace.Entries[2].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x2 opcode");
            Assert.That(trace.Entries[2].Storage, Is.EquivalentTo(new Dictionary<string, string>
            {
                [Slot("2")] = ZeroWord,
            }), "SSTORE 0x2 snapshot");

            Assert.That(trace.Entries[5].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x3 opcode");
            Assert.That(trace.Entries[5].Storage, Is.EquivalentTo(new Dictionary<string, string>
            {
                [Slot("2")] = ZeroWord,
                [Slot("3")] = ZeroWord,
            }), "SSTORE 0x3 cumulative snapshot");

            // Depth 2 is a fresh frame: it shows only its own slot 0x1 and does not inherit the parent's 0x2/0x3.
            Assert.That(trace.Entries[16].Opcode, Is.EqualTo("SSTORE"), "SSTORE 0x1 opcode");
            Assert.That(trace.Entries[16].Storage, Is.EquivalentTo(new Dictionary<string, string>
            {
                [Slot("1")] = ZeroWord,
            }), "SSTORE 0x1 snapshot (no parent slots inherited)");
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

        Assert.That(trace.Entries[0].Stack.Count, Is.EqualTo(0), "entry[0] length");

        Assert.That(trace.Entries[1].Stack.Count, Is.EqualTo(1), "entry[1] length");
        Assert.That(trace.Entries[1].Stack[0], Is.EqualTo($"0x{SampleHexData1}"), "entry[1][0]");

        Assert.That(trace.Entries[2].Stack.Count, Is.EqualTo(2), "entry[2] length");
        Assert.That(trace.Entries[2].Stack[0], Is.EqualTo($"0x{SampleHexData1}"), "entry[2][0]");
        Assert.That(trace.Entries[2].Stack[1], Is.EqualTo("0x0"), "entry[2][1]");

        Assert.That(trace.Entries[3].Stack.Count, Is.EqualTo(1), "entry[3] length");
        Assert.That(trace.Entries[3].Stack[0], Is.EqualTo($"0x{SampleHexData1}"), "entry[3][0]");
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

        Assert.That(trace.Entries[0].Memory.Count, Is.EqualTo(0), "entry[0] length");

        Assert.That(trace.Entries[1].Memory.Count, Is.EqualTo(0), "entry[1] length");

        Assert.That(trace.Entries[2].Memory.Count, Is.EqualTo(0), "entry[2] length");

        Assert.That(trace.Entries[3].Memory.Count, Is.EqualTo(1), "entry[3] length");
        Assert.That(trace.Entries[3].Memory[0], Is.EqualTo($"0x{SampleHexData1.PadLeft(64, '0')}"), "entry[3][0]");

        Assert.That(trace.Entries[4].Memory.Count, Is.EqualTo(1), "entry[4] length");
        Assert.That(trace.Entries[4].Memory[0], Is.EqualTo($"0x{SampleHexData1.PadLeft(64, '0')}"), "entry[4][0]");

        Assert.That(trace.Entries[5].Memory.Count, Is.EqualTo(1), "entry[5] length");
        Assert.That(trace.Entries[5].Memory[0], Is.EqualTo($"0x{SampleHexData1.PadLeft(64, '0')}"), "entry[5][0]");
    }

    [Test]
    public void Can_trace_extcodesize_optimization()
    {
        // From https://github.com/NethermindEth/nethermind/issues/5717
        byte[] code = Bytes.FromHexString("0x60246044607460d1606b60b9603369866833515b6d086c607f3b15749e4886579008320052006f");

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        AssertEntry(trace.Entries[^3], expectedPc: 25, expectedOpcode: "EXTCODESIZE", expectedStackTop: "0x866833515b6d086c607f", expectedStackCount: 8);
        AssertEntry(trace.Entries[^2], expectedPc: 26, expectedOpcode: "ISZERO", expectedStackTop: "0x0", expectedStackCount: 8);
        AssertEntry(trace.Entries[^1], expectedPc: 27, expectedOpcode: "PUSH21", expectedStackTop: "0x1", expectedStackCount: 8);
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

        static string Word(string hex) => "0x" + hex.PadLeft(64, '0');
        Dictionary<string, string> slot1Only = new() { [Word("1")] = Word(val1) };
        Dictionary<string, string> cumulativeBoth = new() { [Word("1")] = Word(val1), [Word("2")] = Word(val2) };

        // Across the two invocations the four SSTORE snapshots must be, in order:
        //   inv1 SSTORE 0x1 -> {0x1}             (only the first slot written so far)
        //   inv1 SSTORE 0x2 -> {0x1, 0x2}
        //   inv2 SSTORE 0x1 -> {0x1, 0x2}        (cumulative; NOT cleared on the first call's return)
        //   inv2 SSTORE 0x2 -> {0x1, 0x2}
        Dictionary<string, string>[] expectedSstoreSnapshots = [slot1Only, cumulativeBoth, cumulativeBoth, cumulativeBoth];

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        List<Dictionary<string, string>> memorySnapshots = trace.Entries
            .Where(e => e.Opcode == "SSTORE")
            .Select(e => e.Storage)
            .ToList();

        Assert.That(memorySnapshots, Has.Count.EqualTo(expectedSstoreSnapshots.Length), "expected four SSTORE entries (two per invocation)");
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expectedSstoreSnapshots.Length; i++)
                Assert.That(memorySnapshots[i], Is.EquivalentTo(expectedSstoreSnapshots[i]), $"in-memory SSTORE snapshot[{i}]");
        }

        // The streaming tracer must produce identical storage snapshots for the same execution.
        JsonElement[] streamedEntries = ExecuteStreamingTracerEntries(code);
        List<Dictionary<string, string>> streamedSnapshots = streamedEntries
            .Where(e => e.GetProperty("op").GetString() == "SSTORE")
            .Select(ToStorageDictionary)
            .ToList();

        Assert.That(streamedSnapshots, Has.Count.EqualTo(expectedSstoreSnapshots.Length), "streaming: expected four SSTORE entries");
        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expectedSstoreSnapshots.Length; i++)
                Assert.That(streamedSnapshots[i], Is.EquivalentTo(expectedSstoreSnapshots[i]), $"streaming SSTORE snapshot[{i}]");
        }
    }

    private static Dictionary<string, string> ToStorageDictionary(JsonElement entry)
    {
        Dictionary<string, string> storage = [];
        foreach (JsonProperty property in entry.GetProperty("storage").EnumerateObject())
            storage[property.Name] = property.Value.GetString()!;
        return storage;
    }

    /// <summary>
    /// Drives <see cref="GethLikeTxDirectStreamingTracer"/> over the supplied code and returns the streamed
    /// per-opcode JSON entries, so the streaming path can be pinned to the in-memory tracer's behaviour.
    /// </summary>
    private JsonElement[] ExecuteStreamingTracerEntries(byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            GethLikeTxDirectStreamingTracer tracer = new(transaction, GethTraceOptions.Default, writer, pipeWriter: null, CancellationToken.None);
            writer.WriteStartArray();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            tracer.BuildResult();
            writer.WriteEndArray();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static void AssertEntry(GethTxTraceEntry entry, long expectedPc, string expectedOpcode, string expectedStackTop, int expectedStackCount)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ProgramCounter, Is.EqualTo(expectedPc));
            Assert.That(entry.Opcode, Is.EqualTo(expectedOpcode));
            Assert.That(entry.Stack[^1], Is.EqualTo(expectedStackTop));
            Assert.That(entry.Stack.Count, Is.EqualTo(expectedStackCount));
        }
    }
}
