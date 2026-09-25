// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using Testably.Abstractions.Testing;
using Nethermind.Core;

namespace Nethermind.Evm.Test.Tracing;

public class GethLikeBlockFileTracerTests : VirtualMachineTestsBase
{
    [TestCase(false)]
    [TestCase(true)]
    public void File_summary_uses_unprefixed_output_and_line_feed(bool returnsData)
    {
        byte[] code = returnsData
            ? Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done
            : [];
        string file = TraceFile(code);
        string output = returnsData ? new string('0', 62) + "2a" : "";
        string gasUsed = returnsData ? "0x12" : "0x0";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(file, Does.EndWith($"{{\"output\":\"{output}\",\"gasUsed\":\"{gasUsed}\"}}\n"));
            Assert.That(file, Does.Not.Contain("\r"));
        }
    }

    [TestCase("negative", 0)]
    [TestCase("unlimited", 5)]
    [TestCase("oneByte", 1)]
    [TestCase("beforeFirstLineFeed", 1)]
    [TestCase("afterFirstLineFeed", 2)]
    [TestCase("insideSecondRecord", 2)]
    [TestCase("beforeSummary", 4)]
    [TestCase("atSummary", 5)]
    public void File_limit_counts_complete_records_including_line_feeds(string boundary, int expectedRecords)
    {
        byte[] code = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Op(Instruction.STOP).Done;
        string unlimited = TraceFile(code);
        string[] records = unlimited.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long firstRecordBytes = Encoding.UTF8.GetByteCount(records[0]) + 1;
        long beforeSummaryBytes = Encoding.UTF8.GetByteCount(string.Join('\n', records[..^1])) + 1;
        long limit = boundary switch
        {
            "negative" => -1,
            "unlimited" => 0,
            "oneByte" => 1,
            "beforeFirstLineFeed" => firstRecordBytes - 1,
            "afterFirstLineFeed" => firstRecordBytes,
            "insideSecondRecord" => firstRecordBytes + 1,
            "beforeSummary" => beforeSummaryBytes - 1,
            "atSummary" => beforeSummaryBytes,
            _ => throw new AssertionException("Unknown boundary")
        };
        string expected = expectedRecords == 0 ? "" : string.Join('\n', records[..expectedRecords]) + "\n";

        Assert.That(TraceFile(code, limit), Is.EqualTo(expected));
    }

    [TestCase(-1)]
    [TestCase(1)]
    public void File_limit_resets_for_each_transaction(long limit)
    {
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();
        Transaction[] transactions = [Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject, Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject];
        Block block = Build.A.Block.WithTransactions(transactions).TestObject;
        using GethLikeBlockFileTracer tracer = new(block, GethTraceOptions.Default with { Limit = limit }, fileSystem, Spec);
        IBlockTracer blockTracer = tracer;
        blockTracer.StartNewBlockTrace(block);
        foreach (Transaction transaction in transactions)
        {
            ITxTracer txTracer = blockTracer.StartNewTxTrace(transaction);
            txTracer.ReportAction(100, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, default, ExecutionType.TRANSACTION);
            txTracer.ReportActionEnd(90, new byte[] { 42 });
            GasConsumed gas = new(21_010, 10);
            txTracer.MarkAsSuccess(TestItem.AddressB, in gas, [42], []);
            blockTracer.EndTxTrace();
        }
        blockTracer.EndBlockTrace();
        Assert.That(tracer.FileNames, Has.Count.EqualTo(2));
        foreach (string fileName in tracer.FileNames)
            Assert.That(fileSystem.File.ReadAllText(fileName), Is.EqualTo(limit < 0 ? "" : "{\"output\":\"2a\",\"gasUsed\":\"0xa\"}\n"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void File_stack_distinguishes_empty_populated_and_disabled(bool disableStack)
    {
        byte[] code = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.POP).Op(Instruction.POP).Op(Instruction.STOP).Done;
        string[] records = TraceFile(code, disableStack: disableStack).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] expectedStacks = ["[]", "[\"0x2a\"]", "[\"0x2a\",\"0x0\"]", "[\"0x2a\"]", "[]"];
        Assert.That(records, Has.Length.EqualTo(expectedStacks.Length + 1));

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < expectedStacks.Length; i++)
            {
                using JsonDocument record = JsonDocument.Parse(records[i]);
                bool hasStack = record.RootElement.TryGetProperty("stack", out JsonElement stack);
                Assert.That(hasStack, Is.True, $"opcode record {i} must include stack");
                if (hasStack)
                    Assert.That(stack.GetRawText(), Is.EqualTo(disableStack ? "null" : expectedStacks[i]), $"opcode record {i}");
            }
        }
    }

    [Test]
    public void File_return_data_follows_call_results([Values] bool enableReturnData, [Values(Instruction.RETURN, Instruction.REVERT)] Instruction exit)
    {
        byte[] code = PrepareReturningCalls(exit);
        string[] records = TraceFile(code, enableReturnData: enableReturnData).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument trace = JsonDocument.Parse("[" + string.Join(',', records) + "]");
        JsonElement[] opcodes = trace.RootElement.EnumerateArray().Where(record => record.TryGetProperty("opName", out _)).ToArray();
        int firstReturnData = Array.FindIndex(opcodes, record => record.GetProperty("opName").GetString() == "RETURNDATASIZE");
        int lastReturnData = Array.FindLastIndex(opcodes, record => record.GetProperty("opName").GetString() == "CALL");
        Assert.That(firstReturnData, Is.GreaterThan(0));
        Assert.That(lastReturnData, Is.GreaterThan(firstReturnData));

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < opcodes.Length; i++)
            {
                bool expected = enableReturnData && i >= firstReturnData && i <= lastReturnData;
                bool present = opcodes[i].TryGetProperty("returnData", out JsonElement returnData);
                Assert.That(present, Is.EqualTo(expected), $"opcode record {i}");
                if (present)
                    Assert.That(returnData.GetString(), Is.EqualTo("0x" + new string('0', 62) + "2a"), $"opcode record {i}");
            }
        }
    }

    [Test]
    public void File_limit_counts_return_data_bytes([Values(-1, 0)] int offset)
    {
        byte[] code = PrepareReturningCalls(Instruction.RETURN);
        string[] records = TraceFile(code, enableReturnData: true).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int firstReturnData = Array.FindIndex(records, record => record.Contains("\"returnData\""));
        Assert.That(firstReturnData, Is.GreaterThan(0));
        long boundary = Encoding.UTF8.GetByteCount(string.Join('\n', records[..(firstReturnData + 1)])) + 1;
        int expectedRecords = firstReturnData + (offset < 0 ? 1 : 2);
        string expected = string.Join('\n', records[..expectedRecords]) + "\n";

        Assert.That(TraceFile(code, boundary + offset, enableReturnData: true), Is.EqualTo(expected));
    }

    [Test]
    public void Requires_active_specification()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new GethLikeBlockFileTracer(Build.A.Block.TestObject, GethTraceOptions.Default, new MockFileSystem(), null!))!;

        Assert.That(exception.ParamName, Is.EqualTo("spec"));
    }

    [Test]
    public void Should_have_file_names_matching_block_and_transactions()
    {
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();

        Block block = Build.A.Block
            .WithTransactions(new[] {
                Build.A.Transaction.WithHash(Keccak.OfAnEmptyString).TestObject,
                Build.A.Transaction.WithHash(Keccak.OfAnEmptySequenceRlp).TestObject
            })
            .TestObject;

        GethLikeBlockFileTracer tracer = new(block, GethTraceOptions.Default, fileSystem, Spec);
        IBlockTracer blockTracer = (IBlockTracer)tracer;

        for (int i = 0; i < block.Transactions.Length; i++)
        {
            Transaction tx = block.Transactions[i];

            blockTracer.StartNewTxTrace(tx);
            blockTracer.EndTxTrace();

            string fileName = tracer.FileNames.Last();

            Assert.That(fileName, Does.Contain($"block_{block.Hash.Bytes[..4].ToHexString(true)}-{i}-{tx.Hash.Bytes[..4].ToHexString(true)}-"));
            Assert.That(fileName, Does.EndWith(".jsonl"));
        }
    }

    [Test]
    public void Memory_field_is_a_single_0x_prefixed_blob()
    {
        // Regression: TraceMemory.ToHexWordList() returns per-word 0x-prefixed chunks. The JSON-lines
        // converter must emit a single contiguous 0x-prefixed memory blob, not a doubled "0x0x..." prefix.
        byte[] code = Prepare.EvmCode
            .PushData(SampleHexData1.PadLeft(64, '0'))
            .PushData(0)
            .Op(Instruction.MSTORE)
            .Op(Instruction.STOP)
            .Done;

        MockFileSystem fileSystem = new();
        fileSystem.Initialize();

        using GethLikeBlockFileTracer tracer = new(Build.A.Block.TestObject, GethTraceOptions.Default with { EnableMemory = true }, fileSystem, Spec);
        ExecuteBlock(tracer, code);

        string fileName = tracer.FileNames.Single();
        string[] lines = fileSystem.File.ReadAllLines(fileName);

        bool anyMemory = false;
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("memory", out JsonElement memory))
                continue;

            anyMemory = true;
            string value = memory.GetString();
            Assert.That(value, Does.StartWith("0x"));
            Assert.That(value, Does.Not.StartWith("0x0x"), "memory must not have a doubled 0x prefix");
            // After the single 0x prefix, the blob is contiguous hex with no embedded prefixes.
            Assert.That(value!.IndexOf("0x", 2), Is.EqualTo(-1), "memory blob must not contain embedded 0x prefixes");
        }

        Assert.That(anyMemory, Is.True, "expected at least one entry to carry a memory blob");
    }

    [Test]
    public void Create_collision_reports_execution_gas_without_action_trace()
    {
        const ulong gasLimit = 100_000;
        byte[] initCode = [0x00];
        (Block block, Transaction transaction) = PrepareInitTx(Activation, gasLimit, initCode);
        Address deploymentAddress = ContractAddress.From(transaction.SenderAddress!, transaction.Nonce);
        TestState.CreateAccount(deploymentAddress, 0, nonce: 1);
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();
        ulong standardIntrinsicGas = IntrinsicGasCalculator.Calculate(transaction, Spec, block.Header.GasLimit).Standard;

        using GethLikeBlockFileTracer tracer = new(
            block,
            GethTraceOptions.Default,
            fileSystem,
            Spec);
        IBlockTracer blockTracer = tracer;
        blockTracer.StartNewBlockTrace(block);
        ITxTracer txTracer = blockTracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), txTracer);
        blockTracer.EndTxTrace();
        blockTracer.EndBlockTrace();

        using JsonDocument summary = JsonDocument.Parse(fileSystem.File.ReadAllText(tracer.FileNames.Single()));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.RootElement.GetProperty("output").GetString(), Is.EqualTo(""));
            Assert.That(summary.RootElement.GetProperty("gasUsed").GetString(), Is.EqualTo($"0x{gasLimit - standardIntrinsicGas:x}"));
        }
    }

    [TestCase(TxType.AccessList)]
    [TestCase(TxType.SetCode)]
    public void Unsupported_transaction_lists_do_not_abort_file_trace(TxType transactionType)
    {
        Transaction transaction = transactionType switch
        {
            TxType.AccessList => Build.A.Transaction
                .WithType(transactionType)
                .WithAccessList(AccessList.Empty)
                .WithHash(Keccak.OfAnEmptyString)
                .TestObject,
            TxType.SetCode => Build.A.Transaction
                .WithType(transactionType)
                .WithAuthorizationCode(new AuthorizationTuple(1, TestItem.AddressF, 0, 0, UInt256.One, UInt256.One))
                .WithHash(Keccak.OfAnEmptyString)
                .TestObject,
            _ => throw new AssertionException($"Unsupported transaction type {transactionType}.")
        };
        Block block = Build.A.Block.WithTransactions([transaction]).TestObject;
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();

        using GethLikeBlockFileTracer tracer = new(
            block,
            GethTraceOptions.Default,
            fileSystem,
            spec: Frontier.Instance);
        IBlockTracer blockTracer = tracer;

        Assert.That(() => blockTracer.StartNewTxTrace(transaction), Throws.Nothing);

        blockTracer.EndTxTrace();
    }

    private byte[] PrepareReturningCalls(Instruction exit)
    {
        byte[] callee = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).Op(exit).Done;
        TestState.CreateAccount(TestItem.AddressC, 1.Ether);
        TestState.InsertCode(TestItem.AddressC, callee, Spec);
        TestState.CreateAccount(TestItem.AddressE, 1.Ether);
        TestState.InsertCode(TestItem.AddressE, Prepare.EvmCode.Op(Instruction.STOP).Done, Spec);
        TestState.Commit(Spec);
        return Prepare.EvmCode.Call(TestItem.AddressC, 50_000).Op(Instruction.RETURNDATASIZE)
            .Op(Instruction.POP).Call(TestItem.AddressE, 50_000).Op(Instruction.STOP).Done;
    }

    private string TraceFile(byte[] code, long limit = 0, bool disableStack = false, bool enableReturnData = false)
    {
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();
        using GethLikeBlockFileTracer tracer = new(Build.A.Block.TestObject, GethTraceOptions.Default with { Limit = limit, DisableStack = disableStack, EnableReturnData = enableReturnData }, fileSystem, Spec);
        ExecuteBlock(tracer, code);
        return fileSystem.File.ReadAllText(tracer.FileNames.Single());
    }
}
