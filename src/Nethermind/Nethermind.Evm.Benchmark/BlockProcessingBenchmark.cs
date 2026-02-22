// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Block-level processing benchmark measuring <see cref="BlockProcessor.ProcessOne"/>
/// with mainnet-like block scenarios under <see cref="Osaka"/> rules.
///
/// Uses <c>[IterationSetup]</c> to recreate world state before each iteration,
/// ensuring measurements reflect cold-state block processing.
///
/// Scenarios:
/// - EmptyBlock, SingleTransfer, Transfers_50, Transfers_200
/// - Eip1559_200, AccessList_50, ContractDeploy_10
/// - ContractCall_200, MixedBlock (100 legacy + 60 EIP-1559 + 30 AL + 10 calls)
/// </summary>
[MemoryDiagnoser]
public class BlockProcessingBenchmark
{
    private static readonly IReleaseSpec Spec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new TestSpecProvider(Osaka.Instance);

    private static readonly byte[] ContractCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    // Minimal bytecode (STOP) for system contract stubs
    private static readonly byte[] StopCode = [0x00];

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressC)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddStorage(new UInt256(2))
        .AddAddress(TestItem.AddressD)
        .AddStorage(new UInt256(10))
        .AddStorage(new UInt256(11))
        .AddStorage(new UInt256(12))
        .Build();

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private Address _sender = null!;

    private IWorldState _stateProvider = null!;
    private IDisposable? _stateScope;
    private BlockProcessor _processor = null!;

    // Pre-built blocks (immutable after GlobalSetup)
    private Block _emptyBlock = null!;
    private Block _singleTransferBlock = null!;
    private Block _transfers50Block = null!;
    private Block _transfers200Block = null!;
    private Block _eip1559_200Block = null!;
    private Block _accessList50Block = null!;
    private Block _contractDeploy10Block = null!;
    private Block _contractCall200Block = null!;
    private Block _mixedBlock = null!;

    private BlockHeader _header = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sender = _senderKey.Address;

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei())
            .WithTimestamp(1)
            .TestObject;

        _emptyBlock = BuildBlock();
        _singleTransferBlock = BuildBlock(BuildLegacyTransfers(1, 0));
        _transfers50Block = BuildBlock(BuildLegacyTransfers(50, 0));
        _transfers200Block = BuildBlock(BuildLegacyTransfers(200, 0));
        _eip1559_200Block = BuildBlock(BuildEip1559Transfers(200, 0));
        _accessList50Block = BuildBlock(BuildAccessListTxs(50, 0));
        _contractDeploy10Block = BuildBlock(BuildContractDeploys(10, 0));
        _contractCall200Block = BuildBlock(BuildContractCalls(200, 0));

        // MixedBlock: 100 legacy + 60 EIP-1559 + 30 access-list + 10 contract calls
        Transaction[] mixedTxs = new Transaction[200];
        int nonce = 0;
        BuildLegacyTransfers(100, nonce).CopyTo(mixedTxs, 0);
        nonce += 100;
        BuildEip1559Transfers(60, nonce).CopyTo(mixedTxs, 100);
        nonce += 60;
        BuildAccessListTxs(30, nonce).CopyTo(mixedTxs, 160);
        nonce += 30;
        BuildContractCalls(10, nonce).CopyTo(mixedTxs, 190);
        _mixedBlock = BuildBlock(mixedTxs);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _stateScope?.Dispose();
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        _stateProvider.CreateAccount(_sender, 1_000_000.Ether());

        // Deploy contract for ContractCall benchmarks
        _stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
        _stateProvider.InsertCode(TestItem.AddressB, ContractCode, Spec);

        // Deploy system contract stubs required by Osaka execution requests
        _stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
        _stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
        _stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
        _stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        CreateProcessor();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _stateScope?.Dispose();
        _stateScope = null;
    }

    private void CreateProcessor()
    {
        EthereumCodeInfoRepository codeInfo = new(_stateProvider);
        EthereumVirtualMachine vm = new(
            new TestBlockhashProvider(),
            SpecProvider,
            LimboLogs.Instance);
        ITransactionProcessor txProc = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            SpecProvider,
            _stateProvider,
            vm,
            codeInfo,
            LimboLogs.Instance);
        IBlockProcessor.IBlockTransactionsExecutor executor =
            new BlockProcessor.BlockValidationTransactionsExecutor(
                new ExecuteTransactionProcessorAdapter(txProc),
                _stateProvider);

        _processor = new BlockProcessor(
            SpecProvider,
            new AlwaysValidBlockValidator(),
            NoBlockRewards.Instance,
            executor,
            _stateProvider,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(txProc, _stateProvider),
            new BlockhashStore(_stateProvider),
            LimboLogs.Instance,
            new WithdrawalProcessor(_stateProvider, LimboLogs.Instance),
            new ExecutionRequestsProcessor(txProc));
    }

    // ── Benchmarks ────────────────────────────────────────────────────────

    [Benchmark]
    public (Block, TxReceipt[]) EmptyBlock()
        => _processor.ProcessOne(_emptyBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) SingleTransfer()
        => _processor.ProcessOne(_singleTransferBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) Transfers_50()
        => _processor.ProcessOne(_transfers50Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark(Baseline = true)]
    public (Block, TxReceipt[]) Transfers_200()
        => _processor.ProcessOne(_transfers200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) Eip1559_200()
        => _processor.ProcessOne(_eip1559_200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) AccessList_50()
        => _processor.ProcessOne(_accessList50Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) ContractDeploy_10()
        => _processor.ProcessOne(_contractDeploy10Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) ContractCall_200()
        => _processor.ProcessOne(_contractCall200Block, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    [Benchmark]
    public (Block, TxReceipt[]) MixedBlock()
        => _processor.ProcessOne(_mixedBlock, ProcessingOptions.NoValidation,
            NullBlockTracer.Instance, Spec, CancellationToken.None);

    // ── Block builder ─────────────────────────────────────────────────────

    private Block BuildBlock(params Transaction[] transactions)
        => Build.A.Block
            .WithHeader(_header)
            .WithTransactions(transactions)
            .TestObject;

    // ── Transaction builders ──────────────────────────────────────────────

    private Transaction[] BuildLegacyTransfers(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei())
                .WithGasLimit(21_000)
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildEip1559Transfers(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei())
                .WithGasLimit(21_000)
                .WithMaxFeePerGas(2.GWei())
                .WithMaxPriorityFeePerGas(1.GWei())
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildAccessListTxs(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressC)
                .WithValue(1.Wei())
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei())
                .WithAccessList(SampleAccessList)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractDeploys(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(null)
                .WithData(ContractCode)
                .WithGasLimit(100_000)
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private Transaction[] BuildContractCalls(int count, int startNonce)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((UInt256)(startNonce + i))
                .WithTo(TestItem.AddressB)
                .WithGasLimit(50_000)
                .WithGasPrice(2.GWei())
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    // ── Inline validator ──────────────────────────────────────────────────

    private sealed class AlwaysValidBlockValidator : IBlockValidator
    {
        public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error)
        { error = null; return true; }

        public bool ValidateSuggestedBlock(Block block, BlockHeader parent,
            [NotNullWhen(false)] out string? error, bool validateHashes = true)
        { error = null; return true; }

        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts,
            Block suggestedBlock, [NotNullWhen(false)] out string? error)
        { error = null; return true; }

        public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated,
            [NotNullWhen(false)] out string? error)
        { error = null; return true; }

        public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle,
            [NotNullWhen(false)] out string? error)
        { error = null; return true; }

        public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error)
        { error = null; return true; }

        public bool ValidateWithdrawals(Block block, out string? error)
        { error = null; return true; }
    }
}
