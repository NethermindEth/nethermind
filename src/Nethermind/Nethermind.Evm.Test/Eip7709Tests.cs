// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.Proofs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Covers <c>BLOCKHASH</c> served from the EIP-2935 history contract, per
/// <see href="https://eips.ethereum.org/EIPS/eip-7709">EIP-7709</see>.
/// </summary>
/// <remarks>
/// Run against both EIP-8038 pricing and the plain EIP-2929 pricing that precedes it, so the gas assertions stay
/// honest if the two sets of storage-access constants ever diverge.
/// </remarks>
[TestFixture(true)]
[TestFixture(false)]
public class Eip7709Tests(bool isEip8038Enabled) : VirtualMachineTestsBase
{
    private static readonly Hash256 LeadingZerosHash = new("0x0000001111111111111111111111111111111111111111111111111111111111");

    private readonly IReleaseSpec _spec = new ReleaseSpec
    {
        IsEip2929Enabled = true,
        IsEip2930Enabled = true,
        IsEip2935Enabled = true,
        IsEip7709Enabled = true,
        IsEip8037Enabled = true,
        IsEip8038Enabled = isEip8038Enabled,
    };

    // Past the ring buffer size so that every lookup exercises the `number % HISTORY_SERVE_WINDOW` wrap.
    protected override ulong BlockNumber => 10_000;
    protected override ISpecProvider SpecProvider => field ??= new TestSingleReleaseSpecProvider(_spec);

    private ulong ColdStorageAccess => isEip8038Enabled ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad;

    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
    }

    private static IEnumerable<TestCaseData> ServedHashCases()
    {
        yield return new TestCaseData(1UL, TestItem.KeccakA) { TestName = "{m}_nearest_block" };
        yield return new TestCaseData(Eip2935Constants.BlockHashServeWindow, TestItem.KeccakA) { TestName = "{m}_oldest_served_block" };
        // EIP-2935 writes hashes with leading zeros stripped, so ~1/256 of reads return a span shorter than 32 bytes.
        yield return new TestCaseData(1UL, LeadingZerosHash) { TestName = "{m}_hash_with_leading_zero_bytes" };
    }

    [TestCaseSource(nameof(ServedHashCases))]
    public void Blockhash_returns_hash_from_history_storage(ulong depth, Hash256 hash)
    {
        ulong requestedBlock = BlockNumber - depth;
        SetHistoryHash(requestedBlock, hash);

        CallOutputTracer tracer = ExecuteBlockhashAndReturn(requestedBlock);

        Assert.That(tracer.ReturnValue, Is.EqualTo(hash.Bytes.ToArray()));
    }

    [TestCase(0UL, TestName = "{m}_current_block")]
    [TestCase(1UL, TestName = "{m}_future_block")]
    public void Blockhash_of_unavailable_block_returns_zero(ulong offset)
    {
        CallOutputTracer tracer = ExecuteBlockhashAndReturn(BlockNumber + offset);

        Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
    }

    [Test]
    public void Blockhash_of_never_written_slot_returns_zero()
    {
        CallOutputTracer tracer = ExecuteBlockhashAndReturn(BlockNumber - 1);

        Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
    }

    /// <remarks>
    /// Guards the <c>% HISTORY_SERVE_WINDOW</c> wrap: at block 10,000 the un-wrapped index differs from the wrapped
    /// one, so a hash parked at the un-wrapped index must not be visible.
    /// </remarks>
    [Test]
    public void Blockhash_reads_the_slot_wrapped_by_the_ring_buffer_size()
    {
        ulong requestedBlock = BlockNumber - 1;
        Assert.That(requestedBlock % _spec.Eip2935RingBufferSize, Is.Not.EqualTo(requestedBlock), "test needs a block past the ring buffer");
        TestState.Set(new StorageCell(Eip2935Constants.BlockHashHistoryAddress, new UInt256(requestedBlock)), TestItem.KeccakA.Bytes.ToArray());

        CallOutputTracer tracer = ExecuteBlockhashAndReturn(requestedBlock);

        Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
    }

    [Test]
    public void Blockhash_charges_cold_then_warm_storage_access()
    {
        CallOutputTracer singleTracer = ExecuteCode(BlockhashCode(BlockNumber - 1));
        CallOutputTracer repeatedTracer = ExecuteCode(BlockhashCode(BlockNumber - 1, BlockNumber - 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleTracer.GasSpent, Is.EqualTo(
                GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + ColdStorageAccess + GasCostOf.Base));
            Assert.That(repeatedTracer.GasSpent - singleTracer.GasSpent, Is.EqualTo(
                GasCostOf.VeryLow + GasCostOf.BlockHash + GasCostOf.WarmStateRead + GasCostOf.Base));
        }
    }

    [Test]
    public void Blockhash_slot_prewarmed_by_access_list_is_charged_warm()
    {
        ulong requestedBlock = BlockNumber - 1;
        UInt256 storageIndex = new(requestedBlock % _spec.Eip2935RingBufferSize);
        AccessList warmingHistorySlot = new AccessList.Builder()
            .AddAddress(Eip2935Constants.BlockHashHistoryAddress).AddStorage(storageIndex).Build();
        // Same intrinsic cost, but warms a slot the opcode does not read.
        AccessList warmingUnrelatedSlot = new AccessList.Builder()
            .AddAddress(TestItem.AddressC).AddStorage(storageIndex).Build();

        CallOutputTracer warmTracer = ExecuteCode(BlockhashCode(requestedBlock), accessList: warmingHistorySlot);
        CallOutputTracer coldTracer = ExecuteCode(BlockhashCode(requestedBlock), accessList: warmingUnrelatedSlot);

        Assert.That(coldTracer.GasSpent - warmTracer.GasSpent, Is.EqualTo(ColdStorageAccess - GasCostOf.WarmStateRead));
    }

    /// <remarks>
    /// EIP-7709 applies the effects of <c>SLOAD</c>, and per EIP-2929 <c>SLOAD</c> adds only the
    /// <c>(address, slot)</c> pair to <c>accessed_storage_keys</c> — never the address to <c>accessed_addresses</c>.
    /// A later <c>BALANCE</c> of the history contract must therefore still pay the cold account price.
    /// </remarks>
    [Test]
    public void Blockhash_does_not_warm_the_history_contract_account()
    {
        byte[] balanceOnly = Prepare.EvmCode
            .PushData(Eip2935Constants.BlockHashHistoryAddress)
            .Op(Instruction.BALANCE)
            .Op(Instruction.POP)
            .Done;
        byte[] blockhashThenBalance = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .PushData(Eip2935Constants.BlockHashHistoryAddress)
            .Op(Instruction.BALANCE)
            .Op(Instruction.POP)
            .Done;

        CallOutputTracer balanceTracer = ExecuteCode(balanceOnly);
        CallOutputTracer combinedTracer = ExecuteCode(blockhashThenBalance);

        Assert.That(combinedTracer.GasSpent - balanceTracer.GasSpent, Is.EqualTo(
            GasCostOf.VeryLow + GasCostOf.BlockHash + ColdStorageAccess + GasCostOf.Base),
            "BLOCKHASH must not leave the history contract account warm");
    }

    [Test]
    public void Blockhash_outside_the_serve_window_returns_zero_without_storage_charge()
    {
        // Still inside the ring buffer, so the slot holds a hash that must not be served.
        ulong requestedBlock = BlockNumber - Eip2935Constants.BlockHashServeWindow - 1;
        SetHistoryHash(requestedBlock, TestItem.KeccakA);

        CallOutputTracer resultTracer = ExecuteBlockhashAndReturn(requestedBlock);
        CallOutputTracer gasTracer = ExecuteCode(BlockhashCode(requestedBlock));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resultTracer.ReturnValue, Is.EqualTo(new byte[32]));
            Assert.That(gasTracer.GasSpent, Is.EqualTo(
                GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + GasCostOf.Base));
        }
    }

    [Test]
    public void Blockhash_records_history_storage_read()
    {
        UInt256 storageIndex = new((BlockNumber - 1) % _spec.Eip2935RingBufferSize);
        StorageCell expectedCell = new(Eip2935Constants.BlockHashHistoryAddress, storageIndex);

        ProofTxTracer tracer = Execute(new ProofTxTracer(treatZeroAccountDifferently: false), BlockhashCode(BlockNumber - 1));

        Assert.That(tracer.Storages, Does.Contain(expectedCell));
    }

    [Test]
    public void Blockhash_fails_when_storage_access_cannot_be_paid()
    {
        byte[] code = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Done;
        ulong gasLimit = GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + ColdStorageAccess - 1;

        CallOutputTracer tracer = ExecuteCode(code, gasLimit);

        Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
    }

    private static byte[] BlockhashCode(params ulong[] requestedBlocks)
    {
        Prepare code = Prepare.EvmCode;
        foreach (ulong requestedBlock in requestedBlocks)
        {
            code = code.PushData(requestedBlock).Op(Instruction.BLOCKHASH).Op(Instruction.POP);
        }

        return code.Done;
    }

    private CallOutputTracer ExecuteBlockhashAndReturn(ulong requestedBlock)
    {
        byte[] code = Prepare.EvmCode
            .PushData(requestedBlock)
            .Op(Instruction.BLOCKHASH)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;
        return ExecuteCode(code);
    }

    private CallOutputTracer ExecuteCode(byte[] code, ulong gasLimit = 100_000, AccessList? accessList = null)
    {
        Transaction? transaction = accessList is null
            ? null
            : Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithTo(SenderRecipientAndMiner.Default.Recipient)
                .WithGasLimit(gasLimit)
                .WithGasPrice(1)
                .WithValue(1)
                .WithNonce(TestState.GetNonce(SenderRecipientAndMiner.Default.Sender))
                .WithAccessList(accessList)
                .SignedAndResolved(SenderRecipientAndMiner.Default.SenderKey)
                .TestObject;

        (Block block, Transaction preparedTransaction) = PrepareTx(Activation, gasLimit, code, transaction: transaction);
        CallOutputTracer tracer = new();
        _processor.Execute(preparedTransaction, new BlockExecutionContext(block.Header, Spec), tracer);
        return tracer;
    }

    private void SetHistoryHash(ulong blockNumber, Hash256 hash)
    {
        UInt256 storageIndex = new(blockNumber % _spec.Eip2935RingBufferSize);
        StorageCell storageCell = new(Eip2935Constants.BlockHashHistoryAddress, storageIndex);
        // Mirrors BlockhashStore, which stores the hash with leading zeros stripped.
        TestState.Set(storageCell, hash.Bytes.WithoutLeadingZeros().ToArray());
    }
}

/// <summary>
/// Pins that <c>BLOCKHASH</c> is untouched on forks without EIP-7709: the history contract state is ignored and no
/// storage access is charged.
/// </summary>
[TestFixture]
public class Eip7709DisabledTests : VirtualMachineTestsBase
{
    private static readonly IReleaseSpec Spec7709Disabled = new ReleaseSpec
    {
        IsEip2929Enabled = true,
        IsEip2935Enabled = true,
        IsEip7709Enabled = false,
    };
    private static readonly ISpecProvider Spec7709DisabledProvider = new TestSingleReleaseSpecProvider(Spec7709Disabled);

    protected override ulong BlockNumber => 10_000;
    protected override ISpecProvider SpecProvider => Spec7709DisabledProvider;

    [Test]
    public void Blockhash_ignores_history_storage_and_is_not_charged_for_storage_access()
    {
        ulong requestedBlock = BlockNumber - 1;
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
        TestState.Set(
            new StorageCell(Eip2935Constants.BlockHashHistoryAddress, new UInt256(requestedBlock % Spec7709Disabled.Eip2935RingBufferSize)),
            TestItem.KeccakA.Bytes.ToArray());

        byte[] code = Prepare.EvmCode
            .PushData(requestedBlock)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000, code);
        CallOutputTracer tracer = new();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);

        Assert.That(tracer.GasSpent, Is.EqualTo(
            GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + GasCostOf.Base));
    }
}
