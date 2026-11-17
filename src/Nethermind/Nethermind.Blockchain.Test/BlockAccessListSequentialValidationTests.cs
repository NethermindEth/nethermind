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
using System.Collections.Generic;
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
    private const long ReadWriteGasLimit = 500_000;

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

    [Test]
    public void Sequential_validation_hashes_the_generated_bal_when_rows_were_not_validated(
        [Values(ProcessingOptions.NoValidation, ProcessingOptions.ForceSequentialBlockAccessList)] ProcessingOptions processingOptions,
        [Values] bool verifySequentialBlocks)
    {
        ReadOnlyBlockAccessList suggested = WithExtraAccount(GenerateBlockAccessList(), TestItem.AddressC);

        (BlockAccessListManager balManager, Block block) = RunSequentialValidation(suggested, processingOptions, verifySequentialBlocks);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Header.BlockAccessListHash, Is.EqualTo(EncodedHash(balManager.GeneratedBlockAccessList)));
            Assert.That(block.Header.BlockAccessListHash, Is.Not.EqualTo(suggested.WireHash));
        }
    }

    [Test]
    public void Generated_bal_includes_system_address_fee_recipient_credited_with_zero_fee()
    {
        ReadOnlyBlockAccessList generated = GenerateBlockAccessList(configureBlock: static block => block.Header.Beneficiary = Address.SystemUser);

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

    [TestCaseSource(nameof(SuggestedBlockAccessLists))]
    public void Sequential_verify_only_gives_the_verdict_of_the_generated_bal_without_building_it(
        BlockContent content, Func<ReadOnlyBlockAccessList, ReadOnlyBlockAccessList> suggest, bool expectedAccepted)
    {
        ReadOnlyBlockAccessList suggested = suggest(GenerateBlockAccessList(content));

        bool generatedAccepted = Accepts(suggested, content, verifySequentialBlocks: false, out _, out Block? generatedBlock)
            && generatedBlock!.Header.BlockAccessListHash == suggested.WireHash;
        bool verifiedAccepted = Accepts(suggested, content, verifySequentialBlocks: true, out BlockAccessListManager? balManager, out Block? verifiedBlock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedAccepted, Is.EqualTo(expectedAccepted), "generated");
            Assert.That(verifiedAccepted, Is.EqualTo(expectedAccepted), "verify-only");
            if (verifiedAccepted)
            {
                Assert.That(balManager!.GeneratedBlockAccessList.AccountChanges.Count, Is.Zero);
                Assert.That(verifiedBlock!.GeneratedBlockAccessList, Is.Null);
                Assert.That(verifiedBlock.Header.BlockAccessListHash, Is.EqualTo(suggested.WireHash));
            }
        }
    }

    [Test]
    public void Sequential_verify_only_throws_when_the_executor_skipped_rows()
    {
        // Only the transaction row is wrong, so the post-execution row and the structural check alone accept it.
        ReadOnlyBlockAccessList suggested = WithWrongRecipientBalance(GenerateBlockAccessList());

        Assert.Throws<InvalidOperationException>(() => RunSequentialValidation(suggested,
            verifySequentialBlocks: true, executorOptions: ProcessingOptions.NoValidation));
    }

    private static IEnumerable<TestCaseData> SuggestedBlockAccessLists()
    {
        yield return Case(static bal => bal, true, "Valid");
        yield return Case(static bal => WithExtraAccount(bal, TestItem.AddressC), false, "Extra account");
        yield return Case(static bal => WithWireHash(new ReadOnlyBlockAccessList(
            Array.FindAll(bal.AccountChanges.AsSpan().ToArray(), static account => account.Address != TestItem.AddressB), 0)), false, "Missing account");
        yield return Case(static bal => WithRecipient(bal, []), false, "Missing read");
        yield return Case(static bal => WithRecipient(bal, [ReadSlot + 1]), false, "Different read");
        yield return Case(static bal => WithRecipient(bal, [ReadSlot, ReadSlot + 1]), false, "Extra read");
        yield return Case(WithWrongRecipientBalance, false, "Wrong balance");
        yield return Case(static bal => WithRecipient(bal, [ReadSlot], [.. bal.GetAccountChanges(TestItem.AddressB)!.BalanceChanges, new BalanceChange(2, 1)]),
            false, "Wrong post-execution row");
        yield return Case(static bal => bal, true, "No transactions", BlockContent.NoTransactions);
        yield return Case(static bal => WithExtraAccount(bal, TestItem.AddressC), false, "No transactions, extra account", BlockContent.NoTransactions);
        foreach (BlockContent content in (BlockContent[])[BlockContent.ReadThenWrite, BlockContent.WriteThenRead])
        {
            yield return Case(static bal => bal, true, $"{content}, valid", content);
            yield return Case(static bal => WithWireHash(Build.A.BlockAccessList
                .WithAccountChanges([.. bal.AccountChanges.AsSpan()])
                .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressD).WithStorageReads(ReadSlot).TestObject)
                .TestObject), false, $"{content}, write listed as a read", content);
            yield return Case(static bal => WithStorageReads(bal, TestItem.AddressD, [ReadSlot + 1]), false, $"{content}, extra read", content);
        }

        static TestCaseData Case(Func<ReadOnlyBlockAccessList, ReadOnlyBlockAccessList> suggest, bool accepted, string name,
            BlockContent content = BlockContent.OneTransaction)
            => new TestCaseData(content, suggest, accepted).SetName(name);
    }

    /// <summary>Runs <paramref name="suggested"/> through sequential validation of a block whose header declares its hash.</summary>
    private static bool Accepts(ReadOnlyBlockAccessList suggested, BlockContent content, bool verifySequentialBlocks,
        out BlockAccessListManager? balManager, out Block? block)
    {
        try
        {
            (balManager, block) = RunSequentialValidation(suggested, verifySequentialBlocks: verifySequentialBlocks,
                headerHash: suggested.WireHash, content: content);
            return true;
        }
        catch (BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException)
        {
            (balManager, block) = (null, null);
            return false;
        }
    }

    private static ReadOnlyBlockAccessList WithRecipient(ReadOnlyBlockAccessList bal, ulong[] reads, BalanceChange[]? balanceChanges = null)
        => WithStorageReads(bal, TestItem.AddressB, reads, balanceChanges);

    private static ReadOnlyBlockAccessList WithStorageReads(ReadOnlyBlockAccessList bal, Address address, ulong[] reads, BalanceChange[]? balanceChanges = null)
    {
        ReadOnlyAccountChanges account = bal.GetAccountChanges(address)!;
        return WithWireHash(Build.A.BlockAccessList
            .WithAccountChanges([.. bal.AccountChanges.AsSpan()])
            .WithAccountChanges(new ReadOnlyAccountChanges(account.Address, account.StorageChanges,
                Array.ConvertAll(reads, static slot => (UInt256)slot), balanceChanges ?? account.BalanceChanges, account.NonceChanges, account.CodeChanges))
            .TestObject);
    }

    private static ReadOnlyBlockAccessList WithWrongRecipientBalance(ReadOnlyBlockAccessList bal)
        => WithRecipient(bal, [ReadSlot],
            Array.ConvertAll(bal.GetAccountChanges(TestItem.AddressB)!.BalanceChanges, static change => new BalanceChange(change.Index, change.Value + 1)));

    /// <summary>
    /// Runs the block once on the sequential path with no suggested BAL so the executor
    /// constructs the generated BAL, then re-encodes it as a wire <see cref="ReadOnlyBlockAccessList"/>.
    /// </summary>
    private static ReadOnlyBlockAccessList GenerateBlockAccessList(BlockContent content = BlockContent.OneTransaction, Action<Block>? configureBlock = null)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock(content);
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
        ProcessingOptions processingOptions = ProcessingOptions.None,
        bool verifySequentialBlocks = false,
        Hash256? headerHash = null,
        ProcessingOptions? executorOptions = null,
        BlockContent content = BlockContent.OneTransaction)
    {
        (IWorldState stateProvider, BlockAccessListManager balManager) = CreateFundedAmsterdamSetup(verifySequentialBlocks);
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        FundSender(stateProvider);

        Block block = BuildBlock(content);
        block.Header.BlockAccessListHash = headerHash ?? Keccak.Zero;
        RunSequential(stateProvider, balManager, block, suggested, processingOptions, executorOptions ?? processingOptions);
        return (balManager, block);
    }

    private static void RunSequential(
        IWorldState stateProvider,
        BlockAccessListManager balManager,
        Block block,
        ReadOnlyBlockAccessList? blockAccessList,
        ProcessingOptions processingOptions = ProcessingOptions.None,
        ProcessingOptions? executorOptions = null)
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

        executor.ProcessTransactions(block, executorOptions ?? processingOptions, tracer, CancellationToken.None);
        balManager.SetBlockAccessList(block);
    }

    private static (IWorldState, BlockAccessListManager) CreateFundedAmsterdamSetup(bool verifySequentialBlocks = false)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        TestSingleReleaseSpecProvider specProvider = new(Amsterdam.Instance);
        BlockAccessListManager balManager = new(
            stateProvider,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), specProvider, LimboLogs.Instance))
        {
            VerifySequentialBlocks = verifySequentialBlocks
        };
        return (stateProvider, balManager);
    }

    private static void FundSender(IWorldState stateProvider)
    {
        stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        // The recipient reads ReadSlot, so the BAL carries a storage read.
        stateProvider.CreateAccount(TestItem.AddressB, 0);
        stateProvider.InsertCode(TestItem.AddressB, Prepare.EvmCode.PushData(ReadSlot).Op(Instruction.SLOAD).Done, Amsterdam.Instance);
        // Reads ReadSlot when called without data, and writes 1 to it when called with data.
        stateProvider.CreateAccount(TestItem.AddressD, 0);
        stateProvider.InsertCode(TestItem.AddressD, Prepare.EvmCode
            .Op(Instruction.CALLDATASIZE).Op(Instruction.PUSH1).Data(8).Op(Instruction.JUMPI)
            .Op(Instruction.PUSH1).Data((byte)ReadSlot).Op(Instruction.SLOAD).Op(Instruction.STOP)
            .Op(Instruction.JUMPDEST).Op(Instruction.PUSH1).Data(1).Op(Instruction.PUSH1).Data((byte)ReadSlot).Op(Instruction.SSTORE)
            .Done, Amsterdam.Instance);
        stateProvider.Commit(Amsterdam.Instance);
        stateProvider.CommitTree(0);
    }

    private static Block BuildBlock(BlockContent content = BlockContent.OneTransaction)
    {
        Transaction[] transactions = content switch
        {
            BlockContent.NoTransactions => [],
            BlockContent.ReadThenWrite => [ReadWriteTransaction(0, write: false), ReadWriteTransaction(1, write: true)],
            BlockContent.WriteThenRead => [ReadWriteTransaction(0, write: true), ReadWriteTransaction(1, write: false)],
            _ => [Build.A.Transaction
                .WithNonce(0)
                .WithValue(1)
                .WithGasPrice(0)
                .WithGasLimit(GasCostOf.Transaction + GasCostOf.ColdSLoad + GasCostOf.VeryLow)
                .WithTo(TestItem.AddressB)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject]
        };

        return Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithGasLimit(transactions.Length > 1 ? 2 * ReadWriteGasLimit : GasCostOf.Transaction * 4)
            .WithTransactions(transactions)
            .TestObject;

        static Transaction ReadWriteTransaction(ulong nonce, bool write) => Build.A.Transaction
            .WithNonce(nonce)
            .WithGasPrice(0)
            .WithGasLimit(ReadWriteGasLimit)
            .WithTo(TestItem.AddressD)
            .WithData(write ? [1] : [])
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    public enum BlockContent
    {
        OneTransaction,
        NoTransactions,
        ReadThenWrite,
        WriteThenRead
    }
}
