// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
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

        GethLikeBlockFileTracer tracer = new(block, GethTraceOptions.Default, fileSystem, destroyRefund: 0);
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

        using GethLikeBlockFileTracer tracer = new(Build.A.Block.TestObject, GethTraceOptions.Default with { EnableMemory = true }, fileSystem, destroyRefund: 0);
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
            (long)Spec.GasCosts.DestroyRefund,
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
            Assert.That(summary.RootElement.GetProperty("output").GetString(), Is.EqualTo("0x"));
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
}
