// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.Proofs;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Covers <c>BLOCKHASH</c> served from the EIP-2935 history contract, per
/// <see href="https://eips.ethereum.org/EIPS/eip-7709">EIP-7709</see>.
/// </summary>
/// <remarks>
/// Bogota with EIP-7709 switched on, the way the other flag-gated Bogota EIPs are tested; once Bogota enables it the
/// override becomes a no-op.
/// </remarks>
[TestFixture]
public class Eip7709Tests : VirtualMachineTestsBase
{
    private const ulong ColdStorageAccess = Eip8038Constants.ColdStorageAccess;
    private const int FloorBindingCalldataBytes = 20_000;

    private static readonly Hash256 LeadingZerosHash = new("0x0000001111111111111111111111111111111111111111111111111111111111");
    private static readonly IReleaseSpec Spec7709 = new OverridableReleaseSpec(Bogota.Instance) { IsEip7709Enabled = true };

    // Past the ring buffer size, so every lookup exercises the `number % HISTORY_SERVE_WINDOW` wrap.
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.BogotaBlockTimestamp;
    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(Spec7709);

    private static StorageCell HistoryCell(ulong blockNumber) =>
        new(Eip2935Constants.BlockHashHistoryAddress, new UInt256(blockNumber % Eip2935Constants.RingBufferSize));

    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
    }

    private static IEnumerable<TestCaseData> ServedHashCases()
    {
        yield return new TestCaseData(1UL, TestItem.KeccakA) { TestName = "{m}_nearest_block" };
        yield return new TestCaseData(Eip2935Constants.BlockHashServeWindow, TestItem.KeccakA) { TestName = "{m}_oldest_served_block" };
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
    public void Blockhash_of_never_written_slot_returns_zero_and_is_charged()
    {
        CallOutputTracer tracer = ExecuteBlockhashAndReturn(BlockNumber - 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
            Assert.That(BlockhashGasCosts(BlockNumber - 1), Is.EqualTo(new[] { GasCostOf.BlockHash + ColdStorageAccess }));
        }
    }

    /// <remarks>
    /// The un-wrapped index differs from the wrapped one here, so a hash parked at the un-wrapped index must not be
    /// visible.
    /// </remarks>
    [Test]
    public void Blockhash_reads_the_slot_wrapped_by_the_ring_buffer_size()
    {
        ulong requestedBlock = BlockNumber - 1;
        Assert.That(requestedBlock % Eip2935Constants.RingBufferSize, Is.Not.EqualTo(requestedBlock), "test needs a block past the ring buffer");
        TestState.Set(new StorageCell(Eip2935Constants.BlockHashHistoryAddress, new UInt256(requestedBlock)), TestItem.KeccakA.ToUInt256());

        CallOutputTracer tracer = ExecuteBlockhashAndReturn(requestedBlock);

        Assert.That(tracer.ReturnValue, Is.EqualTo(new byte[32]));
    }

    [Test]
    public void Blockhash_charges_cold_then_warm_storage_access() =>
        Assert.That(BlockhashGasCosts(BlockNumber - 1, BlockNumber - 1), Is.EqualTo(new[]
        {
            GasCostOf.BlockHash + ColdStorageAccess,
            GasCostOf.BlockHash + GasCostOf.WarmStateRead,
        }));

    /// <remarks>Distinct blocks map to distinct slots, so each pays the cold price.</remarks>
    [Test]
    public void Blockhash_of_distinct_blocks_charges_each_slot_cold() =>
        Assert.That(BlockhashGasCosts(BlockNumber - 1, BlockNumber - 2), Is.EqualTo(new[]
        {
            GasCostOf.BlockHash + ColdStorageAccess,
            GasCostOf.BlockHash + ColdStorageAccess,
        }));

    [Test]
    public void Blockhash_slot_prewarmed_by_access_list_is_charged_warm()
    {
        ulong requestedBlock = BlockNumber - 1;
        UInt256 storageIndex = HistoryCell(requestedBlock).Index;
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
    /// Matches execution-specs (ethereum/execution-specs#2619), whose <c>block_hash</c> adds only the
    /// <c>(HISTORY_STORAGE_ADDRESS, slot)</c> pair to <c>accessed_storage_keys</c>, as <c>SLOAD</c> does: a later
    /// <c>BALANCE</c> of the history contract still pays the cold account price.
    /// </remarks>
    [Test]
    public void Blockhash_does_not_warm_the_history_contract_account()
    {
        byte[] code = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .PushData(Eip2935Constants.BlockHashHistoryAddress)
            .Op(Instruction.BALANCE)
            .Op(Instruction.POP)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);

        Assert.That(trace.Entries.Single(static e => e.Opcode == nameof(Instruction.BALANCE)).GasCost,
            Is.EqualTo(Spec7709.GasCosts.ColdAccountAccessCost), "BLOCKHASH must not leave the history contract account warm");
    }

    [Test]
    public void Blockhash_outside_the_serve_window_returns_zero_without_storage_access()
    {
        // Still inside the ring buffer, so the slot holds a hash that must not be served.
        ulong requestedBlock = BlockNumber - Eip2935Constants.BlockHashServeWindow - 1;
        SetHistoryHash(requestedBlock, TestItem.KeccakA);

        CallOutputTracer resultTracer = ExecuteBlockhashAndReturn(requestedBlock);
        (_, BlockAccessListAtIndex bal) = ExecuteWithBlockAccessList(BlockhashCode(requestedBlock), Spec7709);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resultTracer.ReturnValue, Is.EqualTo(new byte[32]));
            Assert.That(BlockhashGasCosts(requestedBlock), Is.EqualTo(new[] { GasCostOf.BlockHash }));
            Assert.That(bal.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress), Is.Null);
        }
    }

    /// <summary>The read is a state access, so it enters a proof's or witness's storage set.</summary>
    [Test]
    public void Blockhash_records_history_storage_read_for_proofs()
    {
        ProofTxTracer tracer = Execute(new ProofTxTracer(treatZeroAccountDifferently: false), BlockhashCode(BlockNumber - 1));

        Assert.That(tracer.Storages, Does.Contain(HistoryCell(BlockNumber - 1)));
    }

    /// <summary>EIP-7928: the read enters the block access list as a storage read, as in execution-specs.</summary>
    [Test]
    public void Blockhash_records_history_storage_read_in_block_access_list()
    {
        (_, BlockAccessListAtIndex bal) = ExecuteWithBlockAccessList(BlockhashCode(BlockNumber - 1), Spec7709);

        AccountChangesAtIndex? history = bal.GetAccountChanges(Eip2935Constants.BlockHashHistoryAddress);
        Assert.That(history?.StorageReads, Does.Contain(HistoryCell(BlockNumber - 1).Index));
    }

    /// <summary>EIP-8279 meters the storage key a cold <c>SLOAD</c> adds to the block access list; BLOCKHASH inherits it.</summary>
    [Test]
    public void Blockhash_extends_the_eip8279_floor_by_one_storage_key()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip7709Enabled = true, IsEip8131Enabled = true, IsEip8279Enabled = true };
        TestState.CreateAccount(SenderRecipientAndMiner.Default.Recipient, 1.Ether);

        (Block block, Transaction tx) = PrepareTx(Activation, 2_000_000, BlockhashCode(BlockNumber - 1, BlockNumber - 1), new byte[FloorBindingCalldataBytes], 1);
        (ulong gasSpent, _) = ExecuteWithBlockAccessList(block, tx, spec);

        Assert.That(gasSpent, Is.EqualTo(IntrinsicGasCalculator.Calculate(tx, spec).FloorGas + Eip8279Constants.StorageKeyBytes * Eip8131Constants.FloorGasPerByte));
    }

    [Test]
    public void Blockhash_fails_when_storage_access_cannot_be_paid()
    {
        byte[] code = Prepare.EvmCode.PushData(BlockNumber - 1).Op(Instruction.BLOCKHASH).Done;
        ulong opcodeGas = BlockhashGasCosts(BlockNumber - 1)[0];

        GethLikeTxTrace affordable = ExecuteAndTrace(OpcodeGasLimit(code, opcodeGas), code);
        GethLikeTxTrace starved = ExecuteAndTrace(OpcodeGasLimit(code, opcodeGas) - 1, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(affordable.Failed, Is.False);
            Assert.That(starved.Failed, Is.True);
            Assert.That(starved.Entries.Last().Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
        }
    }

    /// <summary>Tracers see the hash the opcode pushed, including one stored with leading zeros stripped.</summary>
    [Test]
    public void Blockhash_reports_the_served_hash_to_tracers()
    {
        SetHistoryHash(BlockNumber - 1, LeadingZerosHash);

        ProofTxTracer tracer = Execute(new ProofTxTracer(treatZeroAccountDifferently: false), BlockhashCode(BlockNumber - 1, BlockNumber - 2));

        Assert.That(tracer.BlockHashes, Is.EquivalentTo(new[] { LeadingZerosHash }), "an empty slot reports no hash");
    }

    private ulong OpcodeGasLimit(byte[] code, ulong opcodeGas)
    {
        // Everything but the BLOCKHASH charge, measured on the same transaction with the opcode's cost known.
        GethLikeTxTrace trace = ExecuteAndTrace(code);
        return trace.Gas - trace.Entries.Single(static e => e.Opcode == nameof(Instruction.BLOCKHASH)).GasCost + opcodeGas;
    }

    private ulong[] BlockhashGasCosts(params ulong[] requestedBlocks) =>
        ExecuteAndTrace(BlockhashCode(requestedBlocks)).Entries
            .Where(static e => e.Opcode == nameof(Instruction.BLOCKHASH))
            .Select(static e => e.GasCost)
            .ToArray();

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

    private (ulong GasSpent, BlockAccessListAtIndex Bal) ExecuteWithBlockAccessList(byte[] code, IReleaseSpec spec)
    {
        (Block block, Transaction tx) = PrepareTx(Activation, 100_000, code);
        return ExecuteWithBlockAccessList(block, tx, spec);
    }

    private (ulong GasSpent, BlockAccessListAtIndex Bal) ExecuteWithBlockAccessList(Block block, Transaction tx, IReleaseSpec spec)
    {
        TracedAccessWorldState tracedState = new(TestState, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        TestSpecProvider specProvider = new(spec);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        TransactionProcessor<EthereumGasPolicy> processor = new(
            BlobBaseFeeCalculator.Instance, specProvider, tracedState, vm, new EthereumCodeInfoRepository(tracedState), LimboLogs.Instance);
        CallOutputTracer tracer = new();
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());
        return (tracer.GasSpent, tracedState.GetGeneratingBlockAccessList()!);
    }

    private void SetHistoryHash(ulong blockNumber, Hash256 hash) => TestState.Set(HistoryCell(blockNumber), hash.ToUInt256());
}

/// <summary>
/// Pins that <c>BLOCKHASH</c> is untouched without EIP-7709: the hash comes from the block tree, the history contract
/// is ignored, and no storage access is charged or recorded.
/// </summary>
/// <remarks>
/// Switches EIP-7709 off explicitly rather than relying on Bogota's default, so the fixture keeps testing the disabled
/// path once Bogota enables it.
/// </remarks>
[TestFixture]
public class Eip7709DisabledTests : VirtualMachineTestsBase
{
    private static readonly IReleaseSpec Spec7709Disabled = new OverridableReleaseSpec(Bogota.Instance) { IsEip7709Enabled = false };

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.BogotaBlockTimestamp;
    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(Spec7709Disabled);

    [Test]
    public void Blockhash_ignores_history_storage_and_is_not_charged_for_storage_access()
    {
        ulong requestedBlock = BlockNumber - 1;
        StorageCell historyCell = new(Eip2935Constants.BlockHashHistoryAddress, new UInt256(requestedBlock % Eip2935Constants.RingBufferSize));
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
        TestState.Set(historyCell, TestItem.KeccakA.ToUInt256());

        byte[] code = Prepare.EvmCode
            .PushData(requestedBlock)
            .Op(Instruction.BLOCKHASH)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        GethLikeTxTrace trace = ExecuteAndTrace(code);
        ProofTxTracer proofTracer = Execute(new ProofTxTracer(treatZeroAccountDifferently: false), code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trace.ReturnValue, Is.EqualTo(Keccak.Compute(requestedBlock.ToString()).Bytes.ToArray()), "served by the block-hash provider");
            Assert.That(trace.Entries.Single(static e => e.Opcode == nameof(Instruction.BLOCKHASH)).GasCost, Is.EqualTo(GasCostOf.BlockHash));
            Assert.That(proofTracer.Storages, Does.Not.Contain(historyCell));
        }
    }
}
