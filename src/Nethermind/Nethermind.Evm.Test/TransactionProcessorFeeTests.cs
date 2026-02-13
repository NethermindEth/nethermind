// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs.GnosisForks;

namespace Nethermind.Evm.Test;

public class TransactionProcessorFeeTests
{
    private TestSpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private OverridableReleaseSpec _spec;

    [SetUp]
    public void Setup()
    {
        _spec = new(PragueGnosis.Instance);
        _specProvider = new TestSpecProvider(_spec);

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether());
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown()
    {
        _worldStateCloser.Dispose();
    }

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public void Check_fees_with_fee_collector(bool isTransactionEip1559, bool withFeeCollector)
    {
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
        }

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithGasPrice(10).WithMaxFeePerGas(10)
            .WithType(isTransactionEip1559 ? TxType.EIP1559 : TxType.Legacy).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        FeesTracer tracer = new();
        CompositeBlockTracer compositeTracer = new();
        compositeTracer.Add(tracer);
        compositeTracer.Add(NullBlockTracer.Instance);

        ExecuteAndTrace(block, compositeTracer);

        tracer.Fees.Should().Be(189000);
        tracer.BurntFees.Should().Be(21000);
    }

    private readonly Address SelfDestructAddress = new("0x89aa9b2ce05aaef815f25b237238c0b4ffff6ae3");

    [Test]
    public void Check_fees_with_fee_collector_destroy_coinbase()
    {
        _spec.FeeCollector = TestItem.AddressC;

        _stateProvider.CreateAccount(TestItem.AddressB, 100.Ether());

        byte[] byteCode = Prepare.EvmCode
            .PushData(SelfDestructAddress)
            .Op(Instruction.SELFDESTRUCT)
            .Done;

        Transaction tx = Build.A.Transaction
            .WithGasPrice(10)
            .WithMaxFeePerGas(10)
            .WithChainId(BlockchainIds.Gnosis)
            .WithType(TxType.EIP1559)
            .WithGasLimit(30000000)
            .WithCode(byteCode)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyB).TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(SelfDestructAddress).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(30000000)
            .TestObject;

        FeesTracer tracer = new();
        CompositeBlockTracer compositeTracer = new();
        compositeTracer.Add(tracer);
        compositeTracer.Add(NullBlockTracer.Instance);

        ExecuteAndTrace(block, compositeTracer);

        tracer.Fees.Should().Be(525213);
        tracer.BurntFees.Should().Be(58357);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Check_paid_fees_multiple_transactions(bool withFeeCollector)
    {
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
        }

        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction.WithType(TxType.Legacy)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1)
            .WithGasPrice(10).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(2)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2).WithGasLimit(42000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // tx1: 1 * 21000
        // tx2: (10 - 2) * 21000 = 168000
        tracer.Fees.Should().Be(189000);

        block.GasUsed.Should().Be(42000);
        tracer.BurntFees.Should().Be(84000);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Check_paid_fees_with_blob(bool withFeeCollector)
    {
        UInt256 initialBalance = 0;
        if (withFeeCollector)
        {
            _spec.FeeCollector = TestItem.AddressC;
            initialBalance = _stateProvider.GetBalance(TestItem.AddressC);
        }

        BlockHeader header = Build.A.BlockHeader.WithExcessBlobGas(0).TestObject;

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.Blob)
            .WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(1).TestObject;

        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(1)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx).WithGasLimit(21000).WithHeader(header).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        tracer.Fees.Should().Be(0);

        block.GasUsed.Should().Be(21000);
        tracer.BurntFees.Should().Be(131072);

        if (withFeeCollector)
        {
            UInt256 currentBalance = _stateProvider.GetBalance(TestItem.AddressC);
            (currentBalance - initialBalance).Should().Be(131072);
        }
    }

    [Test]
    public void Check_paid_fees_with_byte_code()
    {
        byte[] byteCode = Prepare.EvmCode
            .CallWithValue(Address.Zero, 0, 1)
            .PushData(1)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .PushData(0)
            .PushData(1)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(2)
            .WithType(TxType.EIP1559).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1).WithGasPrice(10)
            .WithType(TxType.Legacy).WithGasLimit(21000).TestObject;
        Transaction tx3 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(2).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(1)
            .WithType(TxType.EIP1559).WithCode(byteCode)
            .WithGasLimit(60000).TestObject;
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.LondonBlockNumber)
            .WithBeneficiary(TestItem.AddressB).WithBaseFeePerGas(1).WithTransactions(tx1, tx2, tx3)
            .WithGasLimit(102000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // tx1: 2 * 21000
        // tx2: (10 - 1) * 21000
        // tx3: 1 * 60000
        tracer.Fees.Should().Be(291000);

        block.GasUsed.Should().Be(102000);
        tracer.BurntFees.Should().Be(102000);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Should_stop_when_cancellation(bool withCancellation)
    {
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction.WithType(TxType.Legacy)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1)
            .WithGasPrice(10).WithGasLimit(21000).TestObject;
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(2)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2).WithGasLimit(42000).TestObject;

        FeesTracer feesTracer = new();

        CancellationTokenSource source = new();
        CancellationToken token = source.Token;

        CancellationBlockTracer cancellationBlockTracer = new(feesTracer, token);

        BlockReceiptsTracer blockTracer = new();
        blockTracer.SetOtherTracer(cancellationBlockTracer);

        var blkCtx = new BlockExecutionContext(block.Header, _spec);
        blockTracer.StartNewBlockTrace(block);
        {
            var txTracer = blockTracer.StartNewTxTrace(tx1);
            _transactionProcessor.Execute(tx1, blkCtx, txTracer);
            blockTracer.EndTxTrace();
        }

        if (withCancellation)
        {
            source.Cancel();
        }

        try
        {
            var txTracer = blockTracer.StartNewTxTrace(tx2);
            _transactionProcessor.Execute(tx2, blkCtx, txTracer);
            blockTracer.EndTxTrace();
            blockTracer.EndBlockTrace();
        }
        catch (OperationCanceledException) { }

        if (withCancellation)
        {
            // tx1: 1 * 21000
            feesTracer.Fees.Should().Be(21000);
            feesTracer.BurntFees.Should().Be(42000);
        }
        else
        {
            // tx2: (10 - 2) * 21000 = 168000
            feesTracer.Fees.Should().Be(189000);
            feesTracer.BurntFees.Should().Be(84000);
        }
    }

    [Test]
    public void Check_fees_with_free_transaction()
    {
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithType(TxType.EIP1559)
            .WithMaxFeePerGas(3).WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).WithNonce(1).WithIsServiceTransaction(true)
            .WithType(TxType.EIP1559).WithMaxFeePerGas(3)
            .WithMaxPriorityFeePerGas(1).WithGasLimit(21000).TestObject;
        Transaction tx3 = new SystemTransaction();
        Block block = Build.A.Block.WithNumber(0).WithBaseFeePerGas(1)
            .WithBeneficiary(TestItem.AddressB).WithTransactions(tx1, tx2, tx3).WithGasLimit(42000).TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        tracer.Fees.Should().Be(42000);

        block.GasUsed.Should().Be(42000);
        tracer.BurntFees.Should().Be(21000);
    }

    // ── Direct transfer path tests ──────────────────────────────────────────

    [Test]
    public void Direct_path_plain_transfer_matches_slow_path_state()
    {
        // A plain ether transfer should produce identical state via the direct path
        // as it would via the slow (EVM) path.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(1.Wei()).To(TestItem.AddressB)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(1);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
        tracer.Fees.Should().Be(9 * 21000); // premiumPerGas=9 * gas=21000
        tracer.BurntFees.Should().Be(21000); // baseFee=1 * gas=21000
    }

    [Test]
    public void Direct_path_multiple_transfers_in_block_cumulative_state()
    {
        // Multiple plain transfers in the same block must accumulate state correctly.
        _stateProvider.CreateAccount(TestItem.AddressB, 1.Ether());
        _stateProvider.Commit(_specProvider.GenesisSpec);

        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(100.Wei()).To(TestItem.AddressD)
            .TestObject;
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithNonce(1).WithGasPrice(10).WithGasLimit(21000).WithValue(200.Wei()).To(TestItem.AddressD)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx1, tx2).WithGasLimit(42000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        _stateProvider.GetBalance(TestItem.AddressD).Should().Be(300);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
    }

    [Test]
    public void Direct_path_transfer_to_new_account()
    {
        // Transferring to a non-existent account should create it.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(500.Wei()).To(TestItem.AddressE)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        _stateProvider.GetBalance(TestItem.AddressE).Should().Be(500);
    }

    [Test]
    public void Direct_path_self_transfer()
    {
        // Sender sends ETH to themselves – balance should only decrease by gas cost.
        // Do not read balance before Execute; direct path writes to _blockChanges
        // and a prior GetBalance would populate _intraTxCache with the stale value.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(100.Wei()).To(TestItem.AddressA)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        UInt256 gasCost = 10 * (UInt256)21000;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(1.Ether() - gasCost);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
    }

    [Test]
    public void Direct_path_zero_value_transfer()
    {
        // A zero-value transfer should still increment nonce and charge gas.
        // Do not read balance before Execute; direct path writes to _blockChanges
        // and a prior GetBalance would populate _intraTxCache with the stale value.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(0).To(TestItem.AddressB)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
        UInt256 gasCost = 10 * (UInt256)21000;
        _stateProvider.GetBalance(TestItem.AddressA).Should().Be(1.Ether() - gasCost);
    }

    [Test]
    public void Direct_path_skips_precompile_recipient()
    {
        // Transaction to a precompile address must NOT use the direct path.
        // Precompiles have no trie code, so HasCode returns false, but they
        // execute via EVM CALL and charge different gas than a plain transfer.
        Address ecrecoverPrecompile = new("0x0000000000000000000000000000000000000001");
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(100000).WithValue(1.Wei()).To(ecrecoverPrecompile)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(100000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        // Precompile goes through the slow path: ecrecover base cost = 3000, so gas > 21000
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(1);
        _stateProvider.GetBalance(ecrecoverPrecompile).Should().Be(1);
        // Gas used should be more than a plain transfer (21000 + precompile gas)
        tx.SpentGas.Should().BeGreaterThan(21000);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Direct_path_eip1559_fee_calculation(bool withFeeCollector)
    {
        // EIP-1559 fee split must match between direct and slow paths.
        if (withFeeCollector) _spec.FeeCollector = TestItem.AddressD;

        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(5)
            .WithType(TxType.EIP1559).WithGasLimit(21000).WithValue(1.Wei()).To(TestItem.AddressB)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(2).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        FeesTracer tracer = new();
        ExecuteAndTrace(block, tracer);

        // effectiveGasPrice = min(maxFee=10, baseFee=2 + priority=5) = 7
        // premiumPerGas = effectiveGasPrice - baseFee = 7 - 2 = 5
        // beneficiaryFee = 5 * 21000 = 105000
        // burntFees = baseFee * gas = 2 * 21000 = 42000
        tracer.Fees.Should().Be(105000);
        tracer.BurntFees.Should().Be(42000);

        if (withFeeCollector)
        {
            _stateProvider.GetBalance(TestItem.AddressD).Should().Be(42000);
        }
    }

    [Test]
    public void Direct_path_mixed_with_contract_calls_in_block()
    {
        // A block with both plain transfers and contract calls.
        // The direct path should fire for the plain transfer and the slow path
        // for the contract call, with correct cumulative state.
        byte[] code = Prepare.EvmCode.Op(Instruction.STOP).Done;
        _stateProvider.CreateAccount(TestItem.AddressD, 0);
        _stateProvider.InsertCode(TestItem.AddressD, Keccak.Compute(code), code, _specProvider.GenesisSpec);
        _stateProvider.Commit(_specProvider.GenesisSpec);

        // tx1: plain transfer (direct path)
        Transaction tx1 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(10).WithGasLimit(21000).WithValue(100.Wei()).To(TestItem.AddressB)
            .TestObject;

        // tx2: call to contract (slow path)
        Transaction tx2 = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithNonce(1).WithGasPrice(10).WithGasLimit(30000).WithValue(0).To(TestItem.AddressD)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx1, tx2).WithGasLimit(51000)
            .TestObject;

        ExecuteAndTrace(block, NullBlockTracer.Instance);

        _stateProvider.GetBalance(TestItem.AddressB).Should().Be(100);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(2);
    }

    [Test]
    public void Direct_path_insufficient_balance_returns_error()
    {
        // Transfer more than the sender has should fail with InsufficientSenderBalance.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithGasPrice(1).WithGasLimit(21000).WithValue(2.Ether()).To(TestItem.AddressB)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(0).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(NullBlockTracer.Instance);
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(tx);
        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), txTracer);
        tracer.EndTxTrace();

        result.Should().Be(TransactionResult.InsufficientSenderBalance);
        _stateProvider.GetNonce(TestItem.AddressA).Should().Be(0); // nonce unchanged
    }

    [Test]
    public void Direct_path_wrong_nonce_returns_error()
    {
        // Transaction with wrong nonce should fail.
        Transaction tx = Build.A.Transaction
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .WithNonce(5).WithGasPrice(10).WithGasLimit(21000).WithValue(1.Wei()).To(TestItem.AddressB)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(TestItem.AddressC).WithBaseFeePerGas(1).WithTransactions(tx).WithGasLimit(21000)
            .TestObject;

        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(NullBlockTracer.Instance);
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(tx);
        TransactionResult result = _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), txTracer);
        tracer.EndTxTrace();

        result.Should().Be(TransactionResult.TransactionNonceTooHigh);
    }

    private void ExecuteAndTrace(Block block, IBlockTracer otherTracer)
    {
        BlockReceiptsTracer tracer = new();
        tracer.SetOtherTracer(otherTracer);

        tracer.StartNewBlockTrace(block);
        foreach (Transaction tx in block.Transactions)
        {
            var txTracer = tracer.StartNewTxTrace(tx);
            _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, _spec), txTracer);
            tracer.EndTxTrace();
        }
        tracer.EndBlockTrace();
    }
}
