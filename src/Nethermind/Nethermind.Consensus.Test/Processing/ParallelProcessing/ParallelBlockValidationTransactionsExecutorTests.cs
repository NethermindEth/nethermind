// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutorTests
{
    private static readonly EthereumEcdsa Ecdsa = new(BlockchainIds.Mainnet);

    public class ParallelTestBlockchain(IBlocksConfig blocksConfig) : TestBlockchain
    {
        public static async Task<ParallelTestBlockchain> Create(IBlocksConfig blocksConfig, Action<ContainerBuilder> configurer = null)
        {
            ParallelTestBlockchain chain = new(blocksConfig);
            await chain.Build(configurer);
            return chain;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        protected override IEnumerable<IConfig> CreateConfigs() => [blocksConfig];

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance));
    }

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
            yield return Test("Balance changes across transactions",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 10.Ether()),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1, 5.Ether()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressD, 0, 3.Ether()),
            ]);
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
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithValue(value ?? 1.Ether())
            .WithGasLimit(gasLimit)
            .WithCode(data)
            .SignedAndResolved(from, false)
            .TestObject;

    private static Transaction Tx1559WithHighMaxFee(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(1000000.GWei())
            .WithMaxPriorityFeePerGas(1.GWei())
            .WithGasLimit(21000)
            .SignedAndResolved(from, false)
            .WithValue(1.Wei())
            .TestObject;

    private static Transaction Tx1559WithNegativePremium(PrivateKey from, Address to, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .WithMaxFeePerGas(1.GWei())
            .WithMaxPriorityFeePerGas(10.GWei())
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
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        using ParallelTestBlockchain single = await ParallelTestBlockchain.Create(BuildConfig(false));
        Block block = await parallel.AddBlock(transactions);
        Block singleBlock = await single.AddBlock(transactions);

        Assert.Multiple(() =>
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length));
            Assert.That(singleBlock.Transactions, Has.Length.EqualTo(transactions.Length));
            Assert.That(block.Header.GasUsed, Is.EqualTo(singleBlock.Header.GasUsed));
            Assert.That(block.Header.StateRoot, Is.EqualTo(singleBlock.Header.StateRoot));
        });
    }

    public static IEnumerable<TestCaseData> FailedBlocksTests
    {
        get
        {
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 1)],
                TransactionResult.WrongTransactionNonce);

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ], TransactionResult.WrongTransactionNonce, "nonce gap on dependent transaction");

            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)
            ], TransactionResult.WrongTransactionNonce, "nonce reuse on dependent transaction");

            AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
            yield return Test(
            [
                TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0),
            ], TransactionResult.WrongTransactionNonce, "nonce reuse of SetCode authorization");

            yield return Test([Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)],
                TransactionResult.InsufficientSenderBalance);

            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether(), 100)],
                TransactionResult.GasLimitBelowIntrinsicGas);

            yield return Test([TxWithoutSender(TestItem.AddressB, 0)],
                TransactionResult.SenderNotSpecified);

            // // InsufficientMaxFeePerGasForSenderBalance - EIP-1559 tx
            // yield return Test([Tx1559WithHighMaxFee(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
            //     TransactionResult.InsufficientMaxFeePerGasForSenderBalance);
            //
            // // MinerPremiumNegative - maxPriorityFeePerGas > maxFeePerGas
            // yield return Test([Tx1559WithNegativePremium(TestItem.PrivateKeyA, TestItem.AddressB, 0)],
            //     TransactionResult.MinerPremiumNegative);

            // TransactionSizeOverMaxInitCodeSize - EIP-3860
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, gasLimit: 10_000_000, data: new byte[50000])],
                TransactionResult.TransactionSizeOverMaxInitCodeSize);


            // yield return Test(
            // [
            //     Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether()),
            //     Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0, 1.Ether() / 2),
            //     Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1, 1.Ether()),
            // ], TransactionResult.InsufficientSenderBalance, "on dependent transaction");
        }
    }

    [TestCaseSource(nameof(FailedBlocksTests))]
    public async Task Failed_blocks(Transaction[] transactions, TransactionResult expected)
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        Block block = Build.A.Block.WithTransactions(transactions).WithParent(head).TestObject;
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
            .WithTransactions([tx])
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
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        using ParallelTestBlockchain single = await ParallelTestBlockchain.Create(BuildConfig(false));

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

        Transaction[] transactions = [TxCreateContract(TestItem.PrivateKeyA, 0, create2InitCode, 10.Ether())];

        Block block = await parallel.AddBlock(transactions);
        Block singleBlock = await single.AddBlock(transactions);

        Assert.Multiple(() =>
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(1));
            Assert.That(singleBlock.Transactions, Has.Length.EqualTo(1));
            Assert.That(block.Header.StateRoot, Is.EqualTo(singleBlock.Header.StateRoot));
        });
    }

    [Test]
    public async Task Cross_contract_calls_with_value()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        using ParallelTestBlockchain single = await ParallelTestBlockchain.Create(BuildConfig(false));

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

        Transaction[] transactions =
        [
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
        ];

        Block block = await parallel.AddBlock(transactions);
        Block singleBlock = await single.AddBlock(transactions);

        Assert.Multiple(() =>
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(3));
            Assert.That(singleBlock.Transactions, Has.Length.EqualTo(3));
            Assert.That(block.Header.StateRoot, Is.EqualTo(singleBlock.Header.StateRoot));
        });
    }

    [Test]
    public async Task Delegated_account_can_send_transactions()
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));

        AuthorizationTuple auth = Ecdsa.Sign(TestItem.PrivateKeyB, BlockchainIds.Mainnet, TestItem.AddressC, 0);
        Transaction setCodeTx = TxSetCode(TestItem.PrivateKeyA, TestItem.AddressB, 0, [auth]);
        await parallel.AddBlock([setCodeTx]);

        Transaction txFromB = Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1);

        BlockHeader head = parallel.BlockTree.Head!.Header;
        Block block = Build.A.Block.WithTransactions([txFromB]).WithParent(head).TestObject;
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

        Assert.That(result, Is.EqualTo(TransactionResult.Ok));
    }

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };
}
