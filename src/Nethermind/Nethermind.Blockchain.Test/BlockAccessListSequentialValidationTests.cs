// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Threading;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Covers BAL generation and the column-index fast path on the sequential execution path,
/// reachable only because <c>MergeAndReturnBal</c> feeds each per-tx slice into the generated
/// validation index.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListSequentialValidationTests
{
    private const ulong ReadSlot = 1;

    [Test]
    public void Sequential_validation_accepts_matching_bal_and_populates_generated_index()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        BlockAccessListManager balManager = null!;
        Assert.DoesNotThrow(() => (balManager, _) = RunSequentialValidation(generated));

        // The generated validation index is what gates TryFastPath, so confirm the sequential path
        // actually populated it. Without this wiring the fast path is unreachable and validation
        // silently falls back to the slow path, masking a regression that this assertion catches.
        Assert.That(balManager.HasGeneratedValidationIndexUpdates, Is.True);
    }

    [Test]
    public void Sequential_validation_rejects_mismatched_bal()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();

        // Tamper: an extra storage read on the sender no longer matches what re-execution
        // produces, so the fast-path row compare diverges and the fallback walk must reject.
        ReadOnlyBlockAccessList tampered = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads((UInt256)1)
                .TestObject)
            .TestObject;

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => RunSequentialValidation(tampered));
    }

    [Test]
    public void Sequential_validation_takes_the_wire_hash_without_re_encoding()
    {
        ReadOnlyBlockAccessList suggested = GenerateBlockAccessList();

        (BlockAccessListManager balManager, Block block) = RunSequentialValidation(suggested);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(suggested.WireHash));
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(EncodedHash(balManager.GeneratedBlockAccessList)));
            Assert.That(block.EncodedBlockAccessList, Is.Null);
            Assert.That(block.GeneratedBlockAccessList, Is.SameAs(balManager.GeneratedBlockAccessList));
        }
    }

    [Test]
    public void Sequential_validation_rejects_bal_differing_only_in_structure()
    {
        // An extra account with no changes matches every row, so only the structural check sees it.
        ReadOnlyBlockAccessList suggested = WithExtraAccount(GenerateBlockAccessList(), TestItem.AddressC);

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => RunSequentialValidation(suggested));
    }

    [TestCase(new ulong[0], TestName = "Missing read")]
    [TestCase(new[] { ReadSlot + 1 }, TestName = "Different read")]
    public void Sequential_validation_rejects_bal_differing_only_in_storage_reads(ulong[] recipientReads)
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList();
        ReadOnlyAccountChanges recipient = generated.GetAccountChanges(TestItem.AddressB)!;
        Assert.That(recipient.StorageReads, Is.EqualTo(new UInt256[] { ReadSlot }));

        ReadOnlyAccountChanges tamperedRecipient = new(recipient.Address, recipient.StorageChanges,
            Array.ConvertAll(recipientReads, static slot => (UInt256)slot), recipient.BalanceChanges, recipient.NonceChanges, recipient.CodeChanges);

        Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
            () => RunSequentialValidation(WithWireHash(Build.A.BlockAccessList
                .WithAccountChanges([.. generated.AccountChanges.AsSpan()])
                .WithAccountChanges(tamperedRecipient)
                .TestObject)));
    }

    [TestCase(ProcessingOptions.NoValidation)]
    [TestCase(ProcessingOptions.ForceSequentialBlockAccessList)]
    public void Sequential_validation_defers_mismatched_bal_when_requested(ProcessingOptions processingOptions)
    {
        ReadOnlyBlockAccessList tampered = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads((UInt256)1)
                .TestObject)
            .TestObject;

        Assert.DoesNotThrow(() => RunSequentialValidation(tampered, processingOptions));
    }

    [TestCase(ProcessingOptions.NoValidation)]
    [TestCase(ProcessingOptions.ForceSequentialBlockAccessList)]
    public void Sequential_validation_hashes_the_generated_bal_when_rows_were_not_validated(ProcessingOptions processingOptions)
    {
        ReadOnlyBlockAccessList suggested = WithExtraAccount(GenerateBlockAccessList(), TestItem.AddressC);

        (BlockAccessListManager balManager, Block block) = RunSequentialValidation(suggested, processingOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(EncodedHash(balManager.GeneratedBlockAccessList)));
            Assert.That(block.Header.BlockAccessListHash, Is.Not.EqualTo(suggested.WireHash));
        }
    }

    [Test]
    public void Generated_bal_includes_system_address_fee_recipient_credited_with_zero_fee()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList(static block => block.Header.Beneficiary = Address.SystemUser);

        // EIP-7928 BAL generation records touched accounts even when no state value changes.
        ReadOnlyAccountChanges? systemAccount = generated.GetAccountChanges(Address.SystemUser);
        Assert.That(systemAccount, Is.Not.Null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(systemAccount!.BalanceChanges, Is.Empty);
            Assert.That(systemAccount.NonceChanges, Is.Empty);
            Assert.That(systemAccount.CodeChanges, Is.Empty);
            Assert.That(systemAccount.StorageChanges, Is.Empty);
            Assert.That(systemAccount.StorageReads, Is.Empty);
        }
    }

    /// <summary>
    /// Runs the block once on the sequential path with no suggested BAL so the executor
    /// constructs the generated BAL, then re-encodes it as a wire <see cref="ReadOnlyBlockAccessList"/>.
    /// </summary>
    private static ReadOnlyBlockAccessList GenerateBlockAccessList(Action<Block>? configureBlock = null)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        configureBlock?.Invoke(block);
        RunSequential(stateProvider, balManager, block, blockAccessList: null);

        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(balManager.GeneratedBlockAccessList);
        return Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;
    }

    private static Hash256 EncodedHash(GeneratedBlockAccessList generated)
        => new(ValueKeccak.Compute(BlockAccessListDecoder.EncodeToBytes(generated)));

    private static ReadOnlyBlockAccessList WithExtraAccount(ReadOnlyBlockAccessList bal, Address address)
        => WithWireHash(Build.A.BlockAccessList
            .WithAccountChanges([.. bal.AccountChanges.AsSpan(), Build.An.AccountChanges.WithAddress(address).TestObject])
            .TestObject);

    private static ReadOnlyBlockAccessList WithWireHash(ReadOnlyBlockAccessList bal)
        => Rlp.Decode<ReadOnlyBlockAccessList>(BlockAccessListDecoder.EncodeToBytes(bal))!;

    private static (BlockAccessListManager, Block) RunSequentialValidation(
        ReadOnlyBlockAccessList suggested,
        ProcessingOptions processingOptions = ProcessingOptions.None)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock();
        block.Header.BlockAccessListHash = Keccak.Zero;
        RunSequential(stateProvider, balManager, block, suggested, processingOptions);
        return (balManager, block);
    }

    private static void RunSequential(
        IWorldState stateProvider,
        BlockAccessListManager balManager,
        Block block,
        ReadOnlyBlockAccessList? blockAccessList,
        ProcessingOptions processingOptions = ProcessingOptions.None)
    {
        block.BlockAccessList = blockAccessList;

        IBlockProcessor.IBlockTransactionsExecutor inner = Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>();
        BlockProcessor.ParallelBlockValidationTransactionsExecutor executor = new(
            inner,
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            balManager,
            LimboLogs.Instance);

        BlockExecutionContext executionContext = new(block.Header, Amsterdam.Instance);
        balManager.PrepareForProcessing(block, Amsterdam.Instance, processingOptions);
        executor.SetBlockExecutionContext(executionContext);
        balManager.Setup(block);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        executor.ProcessTransactions(block, processingOptions, tracer, CancellationToken.None);
        balManager.SetBlockAccessList(block);
    }

    private static (IWorldState, BlockAccessListManager) CreateFundedAmsterdamSetup()
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), specProvider, LimboLogs.Instance));
        return (stateProvider, balManager);
    }

    private static void FundSender(IWorldState stateProvider)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        // The recipient reads ReadSlot, so the BAL carries a storage read.
        stateProvider.CreateAccount(TestItem.AddressB, 0);
        stateProvider.InsertCode(TestItem.AddressB, Prepare.EvmCode.PushData(ReadSlot).Op(Instruction.SLOAD).Done, Amsterdam.Instance);
        stateProvider.Commit(Amsterdam.Instance);
        stateProvider.CommitTree(0);
    }

    private static Block BuildBlock()
    {
        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithValue(1)
            .WithGasPrice(0)
            .WithGasLimit(GasCostOf.Transaction + GasCostOf.ColdSLoad + GasCostOf.VeryLow)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(GasCostOf.Transaction * 4)
            .WithTransactions(tx)
            .TestObject;
    }
}
