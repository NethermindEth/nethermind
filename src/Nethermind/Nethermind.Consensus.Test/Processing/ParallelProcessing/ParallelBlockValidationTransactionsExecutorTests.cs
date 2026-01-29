// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing;

[Parallelizable(ParallelScope.All)]
public class ParallelBlockValidationTransactionsExecutorTests
{
    private static readonly EthereumEcdsa Ecdsa = new(BlockchainIds.Mainnet);

    public class ParallelTestBlockchain(IBlocksConfig blocksConfig, IReleaseSpec releaseSpec) : TestBlockchain
    {
        public static async Task<ParallelTestBlockchain> Create(IBlocksConfig blocksConfig, IReleaseSpec releaseSpec = null, Action<ContainerBuilder> configurer = null)
        {
            ParallelTestBlockchain chain = new(blocksConfig, releaseSpec ?? Osaka.Instance);
            await chain.Build(configurer);
            return chain;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        protected override IEnumerable<IConfig> CreateConfigs() => [blocksConfig];

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(releaseSpec));
    }

    /// <summary>
    /// Helper that wraps parallel and single-threaded blockchains for comparison testing.
    /// Executes operations on both chains and provides assertion helpers.
    /// </summary>
    public sealed class DualBlockchain : IAsyncDisposable
    {
        public ParallelTestBlockchain Parallel { get; }
        public ParallelTestBlockchain Single { get; }

        private DualBlockchain(ParallelTestBlockchain parallel, ParallelTestBlockchain single)
        {
            Parallel = parallel;
            Single = single;
        }

        public static async Task<DualBlockchain> Create(IReleaseSpec releaseSpec = null)
        {
            ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true), releaseSpec);
            ParallelTestBlockchain single = await ParallelTestBlockchain.Create(BuildConfig(false), releaseSpec);
            return new DualBlockchain(parallel, single);
        }

        public async Task<(Block Parallel, Block Single)> AddBlock(params Transaction[] transactions)
        {
            Block parallel = await Parallel.AddBlock(transactions);
            Block single = await Single.AddBlock(transactions);
            return (parallel, single);
        }

        public async Task AddBlockNoReturn(params Transaction[] transactions)
        {
            await Parallel.AddBlock(transactions);
            await Single.AddBlock(transactions);
        }

        public ValueTask DisposeAsync()
        {
            Parallel.Dispose();
            Single.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public readonly record struct BlockPair(Block Parallel, Block Single)
    {
        public static implicit operator BlockPair((Block Parallel, Block Single) tuple) => new(tuple.Parallel, tuple.Single);

        public void AssertStateRootsMatch() => Assert.That(Parallel.Header.StateRoot, Is.EqualTo(Single.Header.StateRoot));

        public void AssertFullMatch(int expectedTxCount)
        {
            Block p = Parallel, s = Single;
            Assert.Multiple(() =>
            {
                Assert.That(p.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(s.Transactions, Has.Length.EqualTo(expectedTxCount));
                Assert.That(p.Header.GasUsed, Is.EqualTo(s.Header.GasUsed));
                Assert.That(p.Header.StateRoot, Is.EqualTo(s.Header.StateRoot));
            });
        }
    }

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };

    public static IEnumerable<TestCaseData> SimpleBlocksTests
    {
        get
        {
            yield return Test("1 Transaction", [Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)]);

            yield return Test("3 Transactions, nonce dependency",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ]);

            yield return Test("5 Transactions, nonce dependency",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1, 2.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressD, 2, 3.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressE, 3, 4.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressF, 4, 5.Ether()),
            ]);

            yield return Test("Balance changes across transactions",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 10.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1, 5.Ether()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressD, 0, 3.Ether()),
            ]);

            yield return Test("Balance transfers from multiple senders",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressD, 0, 500.Ether()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressD, 0, 500.Ether()),
                Tx(TestItem.PrivateKeyC, TestItem.AddressD, 0, 500.Ether()),
            ]);

            // Stress test with 15 transactions from 3 senders
            PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
            Transaction[] manyTxs = new Transaction[15];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 5; j++)
                {
                    manyTxs[i * 5 + j] = Build.A.Transaction
                        .WithType(TxType.EIP1559)
                        .To(TestItem.AddressD)
                        .WithNonce((UInt256)j)
                        .WithChainId(BlockchainIds.Mainnet)
                        .WithMaxFeePerGas(2.GWei())
                        .WithMaxPriorityFeePerGas(1.GWei())
                        .WithValue(1.Wei())
                        .WithGasLimit(21000)
                        .SignedAndResolved(senders[i], false)
                        .TestObject;
                }
            }
            yield return Test("15 Transactions from 3 senders", manyTxs);
        }
    }

    public static IEnumerable<TestCaseData> ContractBlocksTests
    {
        get
        {
            // State write then read dependency
            byte[] storeCode = Prepare.EvmCode
                .PushData(42)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] storeInitCode = Prepare.EvmCode.ForInitOf(storeCode).Done;
            Address storeContractAddress = ContractAddress.From(TestItem.AddressA, 0);
            yield return Test("State write then read dependency",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, storeInitCode),
                TxToContract(TestItem.PrivateKeyA, storeContractAddress, 1, [])
            ]);

            // SelfDestruct in transaction
            byte[] selfDestructCode = Prepare.EvmCode
                .SELFDESTRUCT(TestItem.AddressB)
                .Done;
            byte[] selfDestructInitCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;
            Address selfDestructAddress = ContractAddress.From(TestItem.AddressA, 0);
            yield return Test("SelfDestruct in transaction",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, selfDestructInitCode, 10.Ether()),
                TxToContract(TestItem.PrivateKeyA, selfDestructAddress, 1, [])
            ]);

            // Contract creation with value transfer
            byte[] storeBalanceCode = Prepare.EvmCode
                .Op(Instruction.SELFBALANCE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] storeBalanceInitCode = Prepare.EvmCode.ForInitOf(storeBalanceCode).Done;
            yield return Test("Contract creation with value transfer",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, storeBalanceInitCode, 5.Ether()),
                TxCreateContract(TestItem.PrivateKeyB, 0, storeBalanceInitCode, 3.Ether()),
            ]);

            // Transient storage across transactions
            byte[] tStorageCode = Prepare.EvmCode
                .Op(Instruction.PUSH0)
                .Op(Instruction.TLOAD)
                .PushData(1)
                .Op(Instruction.ADD)
                .Op(Instruction.PUSH0)
                .Op(Instruction.TSTORE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.TLOAD)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] tStorageInitCode = Prepare.EvmCode.ForInitOf(tStorageCode).Done;
            Address tStorageAddress = ContractAddress.From(TestItem.AddressA, 0);
            yield return Test("Transient storage across transactions",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, tStorageInitCode),
                TxToContract(TestItem.PrivateKeyA, tStorageAddress, 1, []),
                TxToContract(TestItem.PrivateKeyB, tStorageAddress, 0, []),
            ]);

            // Multiple senders with state dependencies
            byte[] storeCallerCode = Prepare.EvmCode
                .Op(Instruction.PUSH0)
                .Op(Instruction.SLOAD)
                .Op(Instruction.CALLER)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SLOAD)
                .Op(Instruction.SSTORE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SLOAD)
                .PushData(1)
                .Op(Instruction.ADD)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] storeCallerInitCode = Prepare.EvmCode.ForInitOf(storeCallerCode).Done;
            Address storeCallerAddress = ContractAddress.From(TestItem.AddressA, 0);
            yield return Test("Multiple senders with state dependencies",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, storeCallerInitCode),
                TxToContract(TestItem.PrivateKeyA, storeCallerAddress, 1, []),
                TxToContract(TestItem.PrivateKeyB, storeCallerAddress, 0, []),
                TxToContract(TestItem.PrivateKeyC, storeCallerAddress, 0, []),
            ]);

            // Contract deployment and immediate call
            byte[] simpleCode = Prepare.EvmCode
                .Op(Instruction.CALLVALUE)
                .Op(Instruction.PUSH0)
                .Op(Instruction.SSTORE)
                .STOP()
                .Done;
            byte[] simpleInitCode = Prepare.EvmCode.ForInitOf(simpleCode).Done;
            Address simpleAddress = ContractAddress.From(TestItem.AddressA, 0);
            yield return Test("Contract deployment and immediate call",
            [
                TxCreateContract(TestItem.PrivateKeyA, 0, simpleInitCode),
                Build.A.Transaction
                    .To(simpleAddress)
                    .WithNonce(1)
                    .WithChainId(BlockchainIds.Mainnet)
                    .WithGasLimit(100_000)
                    .WithData([])
                    .SignedAndResolved(TestItem.PrivateKeyA, false)
                    .WithValue(5.Ether())
                    .TestObject
            ]);
        }
    }

    public static IEnumerable<TestCaseData> SetCodeBlocksTests
    {
        get
        {
            // SetCode authorization changes nonce
            AuthorizationTuple authB = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            yield return Test("SetCode authorization changes nonce",
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authB]),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1),
            ]);

            // Multiple SetCode authorizations same block
            AuthorizationTuple authD = Ecdsa.Sign(TestItem.PrivateKeyD, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authE = Ecdsa.Sign(TestItem.PrivateKeyE, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            yield return Test("Multiple SetCode authorizations same block",
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0)]),
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressD, 1, [authD]),
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressE, 2, [authE]),
            ]);

            // Authorization chain B→C, C→D, D→E
            AuthorizationTuple authB2 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authC2 = Ecdsa.Sign(TestItem.PrivateKeyC, BlockchainIds.Mainnet, TestItem.AddressD, 0);
            AuthorizationTuple authD2 = Ecdsa.Sign(TestItem.PrivateKeyD, BlockchainIds.Mainnet, TestItem.AddressE, 0);
            yield return Test("Authorization chain",
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authB2]),
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressC, 1, [authC2]),
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressD, 2, [authD2]),
            ]);

            // Re-delegation: delegate B to C, then B to D
            AuthorizationTuple authB3 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            AuthorizationTuple authB4 = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressD, 1);
            yield return Test("Re-delegation in same block",
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [authB3]),
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 1, [authB4]),
            ]);
        }
    }

    private static Transaction Tx(
        PrivateKey from,
        Address to,
        UInt256 nonce,
        UInt256? value = null,
        long gasLimit = 1_000_000,
        byte[] data = null) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(2.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithValue(value ?? 1.Ether())
            .WithGasLimit(gasLimit)
            .WithCode(data ?? [])
            .SignedAndResolved(from, false)
            .TestObject;

    // MaxFeePerGas * GasLimit must exceed sender balance (1000 ETH)
    // 50_000_000 GWei = 0.05 ETH per gas, * 21000 gas = 1050 ETH > 1000 ETH
    private static Transaction Tx1559WithHighMaxFee(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(50_000_000.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(21000)
            .SignedAndResolved(from, false)
            .WithValue(1.Wei())
            .TestObject;

    // MinerPremiumNegative is triggered when MaxFeePerGas < BaseFee (1 GWei)
    // Setting MaxFeePerGas to 0 makes it impossible to cover the base fee
    private static Transaction Tx1559WithNegativePremium(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(21000)
            .SignedAndResolved(from, false)
            .WithValue(1.Wei())
            .TestObject;

    private static Transaction TxWithoutSender(Address to, UInt256 nonce) =>
        Build.A.Transaction
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(21000)
            .WithValue(1.Wei())
            .TestObject;

    private static Transaction TxToContract(PrivateKey from, Address to, UInt256 nonce, byte[] data) =>
        Build.A.Transaction
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(100_000)
            .WithData(data)
            .SignedAndResolved(from, false)
            .WithValue(0)
            .TestObject;

    private static Transaction TxCreateContract(PrivateKey from, UInt256 nonce, byte[] initCode, UInt256? value = null) =>
        Build.A.Transaction
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithGasLimit(1_000_000)
            .WithCode(initCode)
            .SignedAndResolved(from, false)
            .WithValue(value ?? 0)
            .TestObject;

    private static Transaction TxSetCode(PrivateKey from, Address to, UInt256 nonce, AuthorizationTuple[] authList) =>
        Build.A.Transaction
            .WithType(TxType.SetCode)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(100_000)
            .WithAuthorizationCode(authList)
            .SignedAndResolved(from, false)
            .WithValue(0)
            .TestObject;

    private static TestCaseData Test(string name, Transaction[] transactions) => new([transactions]) { TestName = name };

    private static TestCaseData Test(Transaction[] transactions, TransactionResult expected, string name = "", [CallerArgumentExpression(nameof(expected))] string error = "") =>
        new([transactions, expected]) { TestName = $"{transactions.Length} Transactions, {error.Replace(nameof(TransactionResult) + ".", "")}:{name}" };

    [TestCaseSource(nameof(SimpleBlocksTests))]
    [TestCaseSource(nameof(ContractBlocksTests))]
    [TestCaseSource(nameof(SetCodeBlocksTests))]
    public async Task Successful_blocks(Transaction[] transactions)
    {
        await using DualBlockchain chains = await DualBlockchain.Create();
        BlockPair blocks = await chains.AddBlock(transactions);
        blocks.AssertFullMatch(transactions.Length);
    }

    public static IEnumerable<TestCaseData> FailedBlocksTests
    {
        get
        {
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 1)],
                TransactionResult.TransactionNonceTooHigh);

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ], TransactionResult.TransactionNonceTooHigh, "nonce gap on dependent transaction");

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)
            ], TransactionResult.TransactionNonceTooLow, "nonce reuse on dependent transaction");

            AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            yield return Test(
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0),
            ], TransactionResult.TransactionNonceTooLow, "nonce reuse of SetCode authorization");

            yield return Test([Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)],
                TransactionResult.InsufficientSenderBalance);

            yield return Test([
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)
            ], TransactionResult.InsufficientSenderBalance, "insufficient balance on second transaction");

            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether(), 100)],
                TransactionResult.GasLimitBelowIntrinsicGas);

            yield return Test([TxWithoutSender(TestItem.AddressB, 0)],
                TransactionResult.SenderNotSpecified);

            // InsufficientMaxFeePerGasForSenderBalance - EIP-1559 tx
            yield return Test([Tx1559WithHighMaxFee(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
                TransactionResult.InsufficientMaxFeePerGasForSenderBalance);

            // MinerPremiumNegative - maxPriorityFeePerGas > maxFeePerGas
            yield return Test([Tx1559WithNegativePremium(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
                TransactionResult.MinerPremiumNegative);

            // TransactionSizeOverMaxInitCodeSize - EIP-3860
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, gasLimit: 10_000_000, data: new byte[50000])],
                TransactionResult.TransactionSizeOverMaxInitCodeSize);

            // B has 1000 ETH. tx2 sends 999.5 ETH leaving ~0.5 ETH. tx3 tries to send 1 ETH and fails.
            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0, 999.Ether() + 500000000.GWei()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1, 1.Ether()),
            ], TransactionResult.InsufficientSenderBalance, "on dependent transaction");

            // NonceOverflow - contract creation with max nonce
            yield return Test([TxCreateContract(TestItem.PrivateKeyA, ulong.MaxValue, [0x60, 0x00, 0x60, 0x00, 0xF3])],
                TransactionResult.NonceOverflow);
        }
    }

    [TestCaseSource(nameof(FailedBlocksTests))]
    public async Task Failed_blocks(Transaction[] transactions, TransactionResult expected)
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        Block block = Build.A.Block
            .WithTransactions(transactions)
            .WithParent(head)
            .WithBaseFeePerGas(1.GWei())
            .TestObject;
        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(block.Header);
        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        TransactionResult result = TransactionResult.Ok;
        try
        {
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, releaseSpec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task Failed_block_gas_limit_exceeded()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;

        Transaction tx = Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether(), 100_000);
        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithParent(head)
            .WithGasLimit(21000)
            .TestObject;

        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(block.Header);
        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        TransactionResult result = TransactionResult.Ok;
        try
        {
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, releaseSpec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(TransactionResult.BlockGasLimitExceeded));
    }

    [Test]
    public async Task SelfDestruct_recreate_in_same_transaction()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.AddressB)
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        byte[] salt = new UInt256(123).ToBigEndian();
        Address createAddress = ContractAddress.From(TestItem.AddressA, 0);
        Address contractAddress = ContractAddress.From(createAddress, salt, initCode);

        byte[] create2Code = Prepare.EvmCode
            .Create2(initCode, salt, 1.Ether())
            .Call(contractAddress, 50000)
            .Create2(initCode, salt, 1.Ether())
            .STOP()
            .Done;
        byte[] create2InitCode = Prepare.EvmCode.ForInitOf(create2Code).Done;

        BlockPair blocks = await chains.AddBlock(TxCreateContract(TestItem.PrivateKeyA, 0, create2InitCode, 10.Ether()));
        blocks.AssertFullMatch(1);
    }

    [Test]
    public async Task Cross_contract_calls_with_value()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] receiverCode = Prepare.EvmCode
            .Op(Instruction.CALLVALUE)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] receiverInitCode = Prepare.EvmCode.ForInitOf(receiverCode).Done;
        Address receiverAddress = ContractAddress.From(TestItem.AddressA, 0);

        byte[] callerCode = Prepare.EvmCode
            .CallWithValue(receiverAddress, 50000)
            .STOP()
            .Done;
        byte[] callerInitCode = Prepare.EvmCode.ForInitOf(callerCode).Done;

        BlockPair blocks = await chains.AddBlock(
            TxCreateContract(TestItem.PrivateKeyA, 0, receiverInitCode),
            TxCreateContract(TestItem.PrivateKeyB, 0, callerInitCode, 5.Ether()),
            Build.A.Transaction
                .To(ContractAddress.From(TestItem.AddressB, 0))
                .WithNonce(1)
                .WithChainId(BlockchainIds.Mainnet)
                .WithGasLimit(100_000)
                .SignedAndResolved(TestItem.PrivateKeyB, false)
                .WithValue(2.Ether())
                .TestObject
        );
        blocks.AssertFullMatch(3);
    }

    [Test]
    public async Task Delegated_account_can_send_transactions()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));

        // First block: SetCode authorization that delegates PrivateKeyB to AddressC
        AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
        Transaction setCodeTx = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]);
        Block setCodeBlock = await parallel.AddBlock(setCodeTx);

        Assert.That(setCodeBlock.Transactions, Has.Length.EqualTo(1), "SetCode transaction should be included");

        // Second block: Transaction from the delegated account (PrivateKeyB with nonce=1 after authorization)
        Transaction txFromB = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(TestItem.AddressC)
            .WithNonce(1)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(100_000)
            .WithValue(1.Ether())
            .SignedAndResolved(TestItem.PrivateKeyB, false)
            .TestObject;

        Block txBlock = await parallel.AddBlock(txFromB);

        Assert.That(txBlock.Transactions, Has.Length.EqualTo(1), "Transaction from delegated account should be included");
    }

    [Test]
    public async Task Empty_block_parallel()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();
        BlockPair blocks = await chains.AddBlock();
        blocks.AssertFullMatch(0);
    }

    [Test]
    public async Task Storage_conflicts_with_setup_block()
    {
        // Tests WAW (Write-After-Write) conflicts requiring re-execution
        await using DualBlockchain chains = await DualBlockchain.Create();

        // Contract that increments slot 0
        byte[] incrementerCode = Prepare.EvmCode
            .Op(Instruction.PUSH0)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] incrementerInitCode = Prepare.EvmCode.ForInitOf(incrementerCode).Done;

        await chains.AddBlockNoReturn(TxCreateContract(TestItem.PrivateKeyA, 0, incrementerInitCode));

        Address incrementerAddress = ContractAddress.From(TestItem.AddressA, 0);

        // 3 transactions from different senders all incrementing the same counter
        PrivateKey[] senders = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
        Transaction[] transactions = new Transaction[3];
        for (int i = 0; i < 3; i++)
        {
            int nonce = i == 0 ? 1 : 0;
            transactions[i] = Build.A.Transaction
                .To(incrementerAddress)
                .WithNonce((UInt256)nonce)
                .WithChainId(BlockchainIds.Mainnet)
                .WithGasLimit(100_000)
                .WithData([])
                .SignedAndResolved(senders[i], false)
                .WithValue(0)
                .TestObject;
        }

        BlockPair blocks = await chains.AddBlock(transactions);
        blocks.AssertStateRootsMatch();
    }

    [Test]
    public async Task CREATE2_collision_with_setup_block()
    {
        await using DualBlockchain chains = await DualBlockchain.Create();

        byte[] simpleCode = Prepare.EvmCode
            .PushData(1)
            .Op(Instruction.PUSH0)
            .Op(Instruction.SSTORE)
            .STOP()
            .Done;
        byte[] initCode = Prepare.EvmCode.ForInitOf(simpleCode).Done;
        byte[] salt = new UInt256(12345).ToBigEndian();

        byte[] factoryCode = Prepare.EvmCode
            .Create2(initCode, salt, 0)
            .STOP()
            .Done;
        byte[] factoryInitCode = Prepare.EvmCode.ForInitOf(factoryCode).Done;

        await chains.AddBlockNoReturn(
            TxCreateContract(TestItem.PrivateKeyA, 0, factoryInitCode),
            TxCreateContract(TestItem.PrivateKeyB, 0, factoryInitCode)
        );

        Address factory1 = ContractAddress.From(TestItem.AddressA, 0);
        Address factory2 = ContractAddress.From(TestItem.AddressB, 0);

        BlockPair blocks = await chains.AddBlock(
            TxToContract(TestItem.PrivateKeyA, factory1, 1, []),
            TxToContract(TestItem.PrivateKeyB, factory2, 1, [])
        );
        blocks.AssertStateRootsMatch();
    }

    [Test]
    public async Task SelfDestruct_with_Shanghai_spec()
    {
        // Shanghai spec where SELFDESTRUCT still clears storage for pre-existing contracts
        await using DualBlockchain chains = await DualBlockchain.Create(Shanghai.Instance);

        byte[] selfDestructCode = Prepare.EvmCode
            .SELFDESTRUCT(TestItem.AddressB)
            .Done;
        byte[] selfDestructInitCode = Prepare.EvmCode.ForInitOf(selfDestructCode).Done;

        await chains.AddBlockNoReturn(TxCreateContract(TestItem.PrivateKeyA, 0, selfDestructInitCode, 10.Ether()));

        Address destructorAddress = ContractAddress.From(TestItem.AddressA, 0);
        BlockPair blocks = await chains.AddBlock(TxToContract(TestItem.PrivateKeyA, destructorAddress, 1, []));
        blocks.AssertStateRootsMatch();
    }

    [Test]
    public async Task Failed_sender_has_deployed_code()
    {
        // EIP-3607: Reject transactions from senders with deployed code
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(head);

        // Insert code at TestItem.AddressA (an EOA)
        byte[] code = [0x60, 0x00, 0xF3]; // PUSH 0, RETURN - simple contract code
        Hash256 codeHash = Keccak.Compute(code);

        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        parallel.MainProcessingContext.WorldState.InsertCode(TestItem.AddressA, codeHash.ValueHash256, code, releaseSpec);
        parallel.MainProcessingContext.WorldState.Commit(releaseSpec, NullStateTracer.Instance, commitRoots: true);

        // Now try to send a transaction from that address
        Transaction tx = Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether());
        Block block = Build.A.Block
            .WithTransactions(tx)
            .WithParent(head)
            .WithBaseFeePerGas(1.GWei())
            .TestObject;

        TransactionResult result = TransactionResult.Ok;
        try
        {
            OverridableReleaseSpec spec = (OverridableReleaseSpec)parallel.SpecProvider.GetSpec(block.Header);
            spec.IsEip3607Enabled = true;
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(TransactionResult.SenderHasDeployedCode));
    }
}
