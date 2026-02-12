// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-7778: Block Gas Accounting without Refunds
/// With the revert from ethereum/execution-specs#2073, receipts now use post-refund gas
/// while block gas accounting still uses pre-refund gas.
/// </summary>
public class Eip7778Tests : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider { get; } = CreateSpecProvider();

    private static TestSpecProvider CreateSpecProvider()
    {
        // Use OverridableReleaseSpec to extend London with EIP-7778 enabled
        var eip7778Spec = new OverridableReleaseSpec(London.Instance) { IsEip7778Enabled = true };
        var provider = new TestSpecProvider(London.Instance)
        {
            NextForkSpec = eip7778Spec,
            ForkOnBlockNumber = 1
        };
        return provider;
    }

    [Test]
    public void Block_gas_uses_pre_refund_value_when_eip7778_enabled()
    {
        // This test verifies that block gas accounting uses pre-refund gas
        // when EIP-7778 is enabled
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        // Code that triggers refund by clearing storage
        // PUSH 0, PUSH 0, SSTORE (set storage[0] = 0, triggers refund)
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((1, 0), 100000, code);
        block.Header.GasUsed = 0;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        // When EIP-7778 is enabled, block.GasUsed should reflect pre-refund gas
        // The refund should not reduce the block gas used
        Assert.That(tracer.GasSpent, Is.GreaterThan(0));
        Assert.That(block.Header.GasUsed, Is.GreaterThan(tracer.GasSpent));
    }

    [Test]
    public void Receipt_uses_post_refund_gas_when_eip7778_enabled()
    {
        // After the revert (ethereum/execution-specs#2073), receipts show post-refund gas
        // This is what users pay, while block gas accounting uses pre-refund
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((1, 0), 100000, code);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt = tracer.TxReceipts[0];

        // Receipt gas is post-refund, block gas is pre-refund
        // So receipt.GasUsed < block.GasUsed when there are refunds
        Assert.That(receipt.GasUsed, Is.LessThan(block.Header.GasUsed), "Receipt GasUsed should be post-refund (less than block gas)");
        Assert.That(receipt.GasUsedTotal, Is.EqualTo(receipt.GasUsed), "For single tx, GasUsedTotal equals GasUsed");
    }

    [Test]
    public void GasConsumed_struct_tracks_block_gas_separately()
    {
        long spentGas = 21000;
        long operationGas = 20000;
        long blockGas = 25000;

        GasConsumed gasConsumed = new(spentGas, operationGas, blockGas);

        Assert.That(gasConsumed.SpentGas, Is.EqualTo(spentGas));
        Assert.That(gasConsumed.OperationGas, Is.EqualTo(operationGas));
        Assert.That(gasConsumed.BlockGas, Is.EqualTo(blockGas));
        Assert.That(gasConsumed.EffectiveBlockGas, Is.EqualTo(blockGas));
    }

    [Test]
    public void GasConsumed_effective_block_gas_uses_spent_gas_when_block_gas_is_zero()
    {
        long spentGas = 21000;
        long operationGas = 20000;

        GasConsumed gasConsumed = new(spentGas, operationGas);

        Assert.That(gasConsumed.BlockGas, Is.EqualTo(0));
        Assert.That(gasConsumed.EffectiveBlockGas, Is.EqualTo(spentGas));
    }

    [Test]
    public void GasConsumed_implicit_conversion_from_long()
    {
        long gas = 21000;
        GasConsumed gasConsumed = gas;

        Assert.That(gasConsumed.SpentGas, Is.EqualTo(gas));
        Assert.That(gasConsumed.OperationGas, Is.EqualTo(gas));
        Assert.That(gasConsumed.BlockGas, Is.EqualTo(0));
    }

    [Test]
    public void GasConsumed_implicit_conversion_to_long()
    {
        GasConsumed gasConsumed = new(21000, 20000, 25000);
        long gas = gasConsumed;

        Assert.That(gas, Is.EqualTo(21000));
    }

    [Test]
    public void Legacy_behavior_unchanged_when_eip7778_disabled()
    {
        // Use block 0 where EIP-7778 is not enabled
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((0, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        // Code that triggers refund by clearing storage
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((0, 0), 100000, code);
        block.Header.GasUsed = 0;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);

        // When EIP-7778 is disabled, block.GasUsed equals gas spent (post-refund)
        Assert.That(tracer.GasSpent, Is.GreaterThan(0));
        Assert.That(block.Header.GasUsed, Is.EqualTo(tracer.GasSpent));
    }

    [Test]
    public void Receipt_gas_equals_block_gas_when_eip7778_disabled()
    {
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((0, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((0, 0), 100000, code);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt = tracer.TxReceipts[0];
        // When EIP-7778 is disabled, receipt and block gas are both post-refund
        Assert.That(receipt.GasUsedTotal, Is.EqualTo(block.Header.GasUsed));
        Assert.That(receipt.GasUsed, Is.EqualTo(block.Header.GasUsed));
    }

    [Test]
    public void Block_gas_uses_calldata_floor_when_execution_gas_is_lower()
    {
        // Use a spec with both EIP-7778 and EIP-7623 enabled
        var eip7778And7623Spec = new OverridableReleaseSpec(London.Instance)
        {
            IsEip7778Enabled = true,
            IsEip7623Enabled = true,
            IsEip2028Enabled = true
        };
        var provider = new TestSpecProvider(London.Instance)
        {
            NextForkSpec = eip7778And7623Spec,
            ForkOnBlockNumber = 1
        };

        // Create a transaction with significant calldata to have high floor gas
        // Floor gas = 21000 + tokens * 10
        // For non-zero bytes: tokens = length * 4 (EIP-2028 multiplier)
        // 100 non-zero bytes = 400 tokens = 4000 floor calldata cost + 21000 = 25000 floor gas
        byte[] calldata = new byte[100];
        for (int i = 0; i < calldata.Length; i++)
            calldata[i] = 0xFF;

        // Standard cost = 21000 + 100 * 16 = 22600 (less than floor)
        // Floor cost = 21000 + 400 * 10 = 25000
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(
            new Transaction { Data = calldata, To = Address.Zero },
            eip7778And7623Spec);

        Assert.That(intrinsicGas.Standard, Is.EqualTo(21000 + 100 * 16)); // 22600
        Assert.That(intrinsicGas.FloorGas, Is.EqualTo(21000 + 400 * 10)); // 25000
        Assert.That(intrinsicGas.FloorGas, Is.GreaterThan(intrinsicGas.Standard));

        // Now test execution - the block gas should use the floor since standard < floor
        TestState.CreateAccount(TestItem.AddressA, 1.Ether());
        TestState.Commit(provider.GetSpec((1, 0)));

        var processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, provider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        Transaction tx = Build.A.Transaction
            .WithData(calldata)
            .WithGasLimit(30000)
            .WithGasPrice(1)
            .WithNonce(TestState.GetNonce(TestItem.AddressA))
            .To(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithTimestamp(0)
            .WithTransactions(tx)
            .WithGasLimit(1000000)
            .TestObject;
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        processor.Execute(tx, new BlockExecutionContext(block.Header, provider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt = tracer.TxReceipts[0];

        // Block gas should be max(preRefundGas, floorGas)
        // Since this is a simple transfer with no refunds, preRefundGas = standard gas
        // But floor gas is higher, so block gas should use floor
        Assert.That(block.Header.GasUsed, Is.EqualTo(intrinsicGas.FloorGas), "Block gas should use calldata floor when it exceeds execution gas");
        // Receipt also uses floor gas since it's what user pays
        Assert.That(receipt.GasUsed, Is.EqualTo(intrinsicGas.FloorGas), "Receipt GasUsed should use calldata floor");
    }

    [Test]
    public void Block_gas_uses_execution_gas_when_it_exceeds_calldata_floor()
    {
        // Use a spec with both EIP-7778 and EIP-7623 enabled
        var eip7778And7623Spec = new OverridableReleaseSpec(London.Instance)
        {
            IsEip7778Enabled = true,
            IsEip7623Enabled = true,
            IsEip2028Enabled = true,
            IsEip2200Enabled = true
        };
        var provider = new TestSpecProvider(London.Instance)
        {
            NextForkSpec = eip7778And7623Spec,
            ForkOnBlockNumber = 1
        };

        // Create a transaction with minimal calldata so floor gas is low
        // Floor gas = 21000 + 1 * 10 = 21010 (for 1 zero byte)
        byte[] calldata = new byte[] { 0 };

        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(
            new Transaction { Data = calldata, To = Address.Zero },
            eip7778And7623Spec);

        Assert.That(intrinsicGas.Standard, Is.EqualTo(21000 + 4)); // 21004 for 1 zero byte
        Assert.That(intrinsicGas.FloorGas, Is.EqualTo(21000 + 10)); // 21010 floor

        // Set up sender and recipient accounts
        TestState.CreateAccount(TestItem.AddressA, 1.Ether());
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(provider.GetSpec((1, 0)));

        var processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, provider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        // Code that does an SSTORE operation (expensive - uses way more than floor gas)
        byte[] code = Prepare.EvmCode
            .PushData(1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        TestState.InsertCode(Recipient, code, provider.GenesisSpec);
        TestState.Commit(provider.GetSpec((1, 0)));

        Transaction tx = Build.A.Transaction
            .WithData(calldata)
            .WithGasLimit(100000)
            .WithGasPrice(1)
            .WithNonce(TestState.GetNonce(TestItem.AddressA))
            .To(Recipient)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithTimestamp(0)
            .WithTransactions(tx)
            .WithGasLimit(1000000)
            .TestObject;
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        processor.Execute(tx, new BlockExecutionContext(block.Header, provider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt = tracer.TxReceipts[0];

        // The execution gas (pre-refund) should exceed the floor gas
        // Block gas = max(preRefundGas, floorGas), so should use preRefundGas
        Assert.That(block.Header.GasUsed, Is.GreaterThan(intrinsicGas.FloorGas), "Block gas should exceed calldata floor when execution is more expensive");
        // Receipt uses post-refund gas which is less than block gas
        Assert.That(receipt.GasUsed, Is.LessThanOrEqualTo(block.Header.GasUsed), "Receipt GasUsed (post-refund) should be <= block gas (pre-refund)");
    }

    [Test]
    public void Block_gas_uses_max_of_pre_refund_and_floor_with_refund_scenario()
    {
        // Test the scenario where there's a refund but floor gas still applies
        // Formula: blockGas = max(preRefundGas, floorGas)
        //          receiptGas = max(postRefundGas, floorGas)
        var eip7778And7623Spec = new OverridableReleaseSpec(London.Instance)
        {
            IsEip7778Enabled = true,
            IsEip7623Enabled = true,
            IsEip2028Enabled = true,
            IsEip2200Enabled = true,
            IsEip3529Enabled = true // London refund rules
        };
        var provider = new TestSpecProvider(London.Instance)
        {
            NextForkSpec = eip7778And7623Spec,
            ForkOnBlockNumber = 1
        };

        // Set up sender and storage to clear (triggers refund)
        TestState.CreateAccount(TestItem.AddressA, 1.Ether());
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(provider.GetSpec((1, 0)));

        var processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, provider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        // Code that clears storage (triggers refund)
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        TestState.InsertCode(Recipient, code, provider.GenesisSpec);
        TestState.Commit(provider.GetSpec((1, 0)));

        // Use minimal calldata so floor is low
        byte[] calldata = new byte[] { 0 };

        Transaction tx = Build.A.Transaction
            .WithData(calldata)
            .WithGasLimit(100000)
            .WithGasPrice(1)
            .WithNonce(TestState.GetNonce(TestItem.AddressA))
            .To(Recipient)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block.WithNumber(1)
            .WithTimestamp(0)
            .WithTransactions(tx)
            .WithGasLimit(1000000)
            .TestObject;
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        tracer.StartNewTxTrace(tx);
        processor.Execute(tx, new BlockExecutionContext(block.Header, provider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt = tracer.TxReceipts[0];

        // With refund: receipt.GasUsed (post-refund) < block.GasUsed (pre-refund)
        EthereumIntrinsicGas intrinsicGas = IntrinsicGasCalculator.Calculate(tx, eip7778And7623Spec);

        Assert.That(receipt.GasUsed, Is.LessThan(block.Header.GasUsed), "Receipt shows post-refund gas, which is less than block gas");
        Assert.That(receipt.GasUsed, Is.GreaterThanOrEqualTo(intrinsicGas.FloorGas), "User payment should be at least floor gas");
        Assert.That(block.Header.GasUsed, Is.GreaterThanOrEqualTo(intrinsicGas.FloorGas), "Block gas should be at least floor gas");
    }

    [Test]
    public void Multiple_transactions_cumulative_gas_uses_post_refund_values()
    {
        // After the revert, cumulative receipt gas uses post-refund values
        // Set up storage slot that will be cleared for refund
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        // First transaction clears storage[0] - triggers refund
        byte[] code1 = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        // Second transaction is a simple transfer (no refund, just base gas)
        byte[] code2 = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction tx1) = PrepareTx((1, 0), 100000, code1);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        // Execute first transaction (with refund)
        tracer.StartNewTxTrace(tx1);
        _processor.Execute(tx1, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        long blockGasAfterTx1 = block.Header.GasUsed;
        TxReceipt receipt1 = tracer.TxReceipts[0];

        // Prepare and execute second transaction (no refund)
        Transaction tx2 = Build.A.Transaction
            .WithNonce(TestState.GetNonce(TestItem.PrivateKeyA.Address))
            .WithGasLimit(100000)
            .WithGasPrice(1)
            .To(Recipient)
            .WithCode(code2)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        tracer.StartNewTxTrace(tx2);
        _processor.Execute(tx2, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();

        TxReceipt receipt2 = tracer.TxReceipts[1];

        // Receipt gas is post-refund, block gas is pre-refund
        // First tx had refund, so receipt1.GasUsed < contribution to block gas
        Assert.That(receipt1.GasUsed, Is.LessThan(blockGasAfterTx1), "First tx receipt (post-refund) should be less than its block gas contribution (pre-refund)");

        // Verify cumulative receipt gas uses post-refund values
        Assert.That(receipt2.GasUsedTotal, Is.EqualTo(receipt1.GasUsed + receipt2.GasUsed), "Cumulative receipt gas should be sum of post-refund gas values");

        // Block gas should be greater than cumulative receipt gas due to refunds
        Assert.That(block.Header.GasUsed, Is.GreaterThan(receipt2.GasUsedTotal), "Block gas (pre-refund) should be greater than cumulative receipt gas (post-refund) due to tx1 refund");
    }

    [Test]
    public void Restore_to_snapshot_zero_clears_all_receipts_and_gas()
    {
        // Test that Restore(0) properly clears all receipts and resets gas tracking
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        byte[] code = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction tx1) = PrepareTx((1, 0), 100000, code);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        // Take snapshot before any transactions
        int snapshot = tracer.TakeSnapshot();
        Assert.That(snapshot, Is.EqualTo(0));

        // Execute a transaction
        tracer.StartNewTxTrace(tx1);
        _processor.Execute(tx1, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(1));
        Assert.That(block.Header.GasUsed, Is.GreaterThan(0));

        // Restore to snapshot 0
        tracer.Restore(snapshot);

        // Verify everything is cleared
        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(0), "Receipts should be empty after restore to 0");
        Assert.That(block.Header.GasUsed, Is.EqualTo(0), "Block gas should be reset after restore to 0");
    }

    [Test]
    public void Restore_after_multiple_transactions_maintains_correct_gas_tracking()
    {
        // Test restore after multiple transactions with varying refunds
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Set(new StorageCell(Recipient, 0), new byte[] { 1 });
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        byte[] codeWithRefund = Prepare.EvmCode
            .PushData(0)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        byte[] codeNoRefund = Prepare.EvmCode
            .Op(Instruction.STOP)
            .Done;

        (Block block, Transaction tx1) = PrepareTx((1, 0), 100000, codeWithRefund);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        // Execute first transaction (with refund)
        tracer.StartNewTxTrace(tx1);
        _processor.Execute(tx1, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        // Take snapshot after first tx
        int snapshotAfterTx1 = tracer.TakeSnapshot();
        long blockGasAfterTx1 = block.Header.GasUsed;
        TxReceipt receipt1 = tracer.TxReceipts[0];

        // Execute second transaction
        Transaction tx2 = Build.A.Transaction
            .WithNonce(TestState.GetNonce(TestItem.PrivateKeyA.Address))
            .WithGasLimit(100000)
            .WithGasPrice(1)
            .To(Recipient)
            .WithCode(codeNoRefund)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        tracer.StartNewTxTrace(tx2);
        _processor.Execute(tx2, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(2));
        Assert.That(block.Header.GasUsed, Is.GreaterThan(blockGasAfterTx1));

        // Restore to snapshot after tx1
        tracer.Restore(snapshotAfterTx1);

        // Verify state is restored to after tx1
        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(1), "Should have 1 receipt after restore");
        Assert.That(tracer.TxReceipts[0].GasUsed, Is.EqualTo(receipt1.GasUsed), "First receipt should be unchanged");
        Assert.That(block.Header.GasUsed, Is.EqualTo(blockGasAfterTx1), "Block gas should be restored to post-tx1 value");
    }

    [Test]
    public void Double_restore_works_correctly()
    {
        // Test that multiple restore operations work correctly
        TestState.CreateAccount(Recipient, 1.Ether());
        TestState.Commit(SpecProvider.GetSpec((1, 0)));

        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);

        byte[] code = Prepare.EvmCode.Op(Instruction.STOP).Done;

        (Block block, Transaction tx1) = PrepareTx((1, 0), 50000, code);
        block.Header.GasUsed = 0;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        // Snapshot 0 (empty)
        int snapshot0 = tracer.TakeSnapshot();

        // Execute tx1
        tracer.StartNewTxTrace(tx1);
        _processor.Execute(tx1, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        // Snapshot 1 (after tx1)
        int snapshot1 = tracer.TakeSnapshot();
        long gasAfterTx1 = block.Header.GasUsed;

        // Execute tx2 (call to Recipient which has code deployed)
        Transaction tx2 = Build.A.Transaction
            .WithNonce(TestState.GetNonce(TestItem.PrivateKeyA.Address))
            .WithGasLimit(50000)
            .WithGasPrice(1)
            .To(Recipient)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        tracer.StartNewTxTrace(tx2);
        _processor.Execute(tx2, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        tracer.EndTxTrace();

        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(2));

        // First restore: back to after tx1
        tracer.Restore(snapshot1);
        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(1));
        Assert.That(block.Header.GasUsed, Is.EqualTo(gasAfterTx1));

        // Second restore: back to empty
        tracer.Restore(snapshot0);
        Assert.That(tracer.TxReceipts.Length, Is.EqualTo(0));
        Assert.That(block.Header.GasUsed, Is.EqualTo(0));
    }
}
