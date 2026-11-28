// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Api;
using Nethermind.Arkiv.Execution;
using Nethermind.Arkiv.Native;
using Nethermind.Arkiv.State;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Arkiv.Test;

[TestFixture]
[NonParallelizable]
public class ArkivTransactionProcessorTests
{
    private string _directory = null!;
    private ArkivChain _chain = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arkiv-tp-tests-" + Guid.NewGuid().ToString("N"));
        _chain = RabdiFixture.OpenChain(_directory, new MemDb(), Substitute.For<IBlockTree>());
    }

    [TearDown]
    public void TearDown()
    {
        // Null when the native library is missing and the test was skipped.
        _chain?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void System_calls_go_to_the_evm()
    {
        ITransactionProcessor evm = Substitute.For<ITransactionProcessor>();
        ArkivTransactionProcessor processor = new(evm, new ArkivBlockContext(), _chain);
        Transaction system = new SystemTransaction { SenderAddress = Address.SystemUser, To = TestItem.AddressA };

        processor.Process(system, NullTxTracer.Instance, ExecutionOptions.Commit);

        evm.Received().Process(system, NullTxTracer.Instance, ExecutionOptions.Commit);
    }

    [Test]
    public void Inside_a_block_each_transaction_takes_its_native_receipt()
    {
        ArkivBlockContext context = new();
        context.Begin(new ArkivBlockResult
        {
            StateRoot = Keccak.Zero,
            GasUsed = 50_000,
            RequestsEmpty = true,
            Receipts = [new ArkivReceipt(true, 21_000, []), new ArkivReceipt(false, 50_000, [])],
            Accounts = [],
            Entities = [],
        });
        ArkivTransactionProcessor processor = new(Substitute.For<ITransactionProcessor>(), context, _chain);
        BlockHeader header = Build.A.BlockHeader.TestObject;
        header.GasUsed = 0;
        processor.SetBlockExecutionContext(header);
        Transaction first = Build.A.Transaction.TestObject, second = Build.A.Transaction.TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processor.Process(first, NullTxTracer.Instance, ExecutionOptions.Commit).TransactionExecuted, Is.True);
            Assert.That(processor.Process(second, NullTxTracer.Instance, ExecutionOptions.Commit).TransactionExecuted, Is.True);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.SpentGas, Is.EqualTo(21_000));
            Assert.That(second.SpentGas, Is.EqualTo(29_000));
            Assert.That(header.GasUsed, Is.EqualTo(50_000));
        }
    }

    [Test]
    public void Tracing_outside_a_block_is_refused()
    {
        ArkivTransactionProcessor processor = new(Substitute.For<ITransactionProcessor>(), new ArkivBlockContext(), _chain);
        processor.SetBlockExecutionContext(Build.A.BlockHeader.TestObject);

        TransactionResult result = processor.Process(Build.A.Transaction.TestObject, NullTxTracer.Instance, ExecutionOptions.SkipValidationAndCommit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Is.EqualTo(ArkivTransactionProcessor.NotTraceable));
        }
    }

    [Test]
    public void A_call_runs_natively_at_the_head()
    {
        // eth_call and eth_estimateGas run with CommitAndRestore, which includes SkipValidation.
        ArkivTransactionProcessor processor = new(Substitute.For<ITransactionProcessor>(), new ArkivBlockContext(), _chain);
        processor.SetBlockExecutionContext(Build.A.BlockHeader.WithNumber(1).WithBaseFee(1_000_000_000).WithGasLimit(30_000_000).TestObject);
        Address owner = TestItem.AddressA;
        Transaction entityNonce = Build.A.Transaction.To(RabdiFixture.ArkivAddress).WithSenderAddress(owner).WithValue(0).WithGasLimit(1_000_000)
            .WithData(Bytes.FromHexString("0x36917bfd000000000000000000000000" + owner.ToString(false, false))).TestObject;
        CallOutputTracer tracer = new();

        TransactionResult result = processor.Process(entityNonce, tracer, ExecutionOptions.CommitAndRestore);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]), "a fresh owner has minted no entity");
        }
    }

    [Test]
    public void Prewarming_does_nothing()
    {
        ITransactionProcessor evm = Substitute.For<ITransactionProcessor>();
        ArkivTransactionProcessor processor = new(evm, new ArkivBlockContext(), _chain);

        TransactionResult result = processor.Process(Build.A.Transaction.TestObject, NullTxTracer.Instance, ExecutionOptions.Warmup | ExecutionOptions.SkipValidation);

        Assert.That(result.TransactionExecuted, Is.True);
        evm.DidNotReceiveWithAnyArgs().Process(default!, default!, default);
    }

    [Test]
    public void Roots_translate_to_the_local_trie_and_unknown_roots_pass()
    {
        ArkivRoots roots = new(_chain);
        Hash256 arkivRoot = TestItem.KeccakA, trieRoot = TestItem.KeccakB;
        BlockHeader header = Build.A.BlockHeader.WithStateRoot(arkivRoot).TestObject;
        _chain.MapRoot(header.Hash!, trieRoot);
        BlockHeader repeated = Build.A.BlockHeader.WithStateRoot(arkivRoot).WithNumber(2).TestObject;
        _chain.MapRoot(repeated.Hash!, TestItem.KeccakC);

        BlockHeader translated = roots.Translate(header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(translated.StateRoot, Is.EqualTo(trieRoot));
            Assert.That(roots.Translate(repeated).StateRoot, Is.EqualTo(TestItem.KeccakC), "a repeated GolemDB root does not overwrite another block's local state");
            Assert.That(translated.Hash, Is.EqualTo(header.Hash));
            Assert.That(header.StateRoot, Is.EqualTo(arkivRoot), "the header itself is left alone");
        }
        BlockHeader unknown = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakC).TestObject;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(roots.Translate(unknown), Is.SameAs(unknown));
            Assert.That(roots.Translate(null), Is.Null);
        }
    }
}
