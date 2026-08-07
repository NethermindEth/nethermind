// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.Proofs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip7709Tests : VirtualMachineTestsBase
{
    private static readonly Hash256 StoredHash = TestItem.KeccakA;
    private static readonly IReleaseSpec Eip7709Spec = new ReleaseSpec
    {
        IsEip2929Enabled = true,
        IsEip2935Enabled = true,
        IsEip7709Enabled = true,
        IsEip8037Enabled = true,
        IsEip8038Enabled = true,
    };
    private static readonly ISpecProvider ReleaseSpecProvider = new TestSingleReleaseSpecProvider(Eip7709Spec);

    protected override ulong BlockNumber => 300;
    protected override ISpecProvider SpecProvider => ReleaseSpecProvider;

    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
    }

    [TestCase(1UL)]
    [TestCase(256UL)]
    public void Blockhash_returns_hash_from_history_storage(ulong depth)
    {
        ulong requestedBlock = BlockNumber - depth;
        SetHistoryHash(requestedBlock, StoredHash);

        CallOutputTracer tracer = ExecuteBlockhashAndReturn(requestedBlock);

        Assert.That(tracer.ReturnValue, Is.EqualTo(StoredHash.Bytes.ToArray()));
    }

    [Test]
    public void Blockhash_charges_cold_then_warm_storage_access()
    {
        byte[] singleLookup = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;
        byte[] repeatedLookup = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;

        CallOutputTracer singleTracer = ExecuteCode(singleLookup);
        CallOutputTracer repeatedTracer = ExecuteCode(repeatedLookup);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleTracer.GasSpent, Is.EqualTo(
                GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + Eip8038Constants.ColdStorageAccess + GasCostOf.Base));
            Assert.That(repeatedTracer.GasSpent - singleTracer.GasSpent, Is.EqualTo(
                GasCostOf.VeryLow + GasCostOf.BlockHash + GasCostOf.WarmStateRead + GasCostOf.Base));
        }
    }

    [Test]
    public void Blockhash_outside_256_block_window_returns_zero_without_storage_charge()
    {
        ulong requestedBlock = BlockNumber - 257;
        SetHistoryHash(requestedBlock, StoredHash);

        CallOutputTracer resultTracer = ExecuteBlockhashAndReturn(requestedBlock);
        byte[] gasCode = Prepare.EvmCode
            .PushData(requestedBlock)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;
        CallOutputTracer gasTracer = ExecuteCode(gasCode);

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
        UInt256 storageIndex = new((BlockNumber - 1) % Eip7709Spec.Eip2935RingBufferSize);
        StorageCell expectedCell = new(Eip2935Constants.BlockHashHistoryAddress, storageIndex);
        byte[] code = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;

        ProofTxTracer tracer = Execute(new ProofTxTracer(treatZeroAccountDifferently: false), code);

        Assert.That(tracer.Storages, Does.Contain(expectedCell));
    }

    [Test]
    public void Blockhash_fails_when_storage_access_cannot_be_paid()
    {
        byte[] code = Prepare.EvmCode
            .PushData(BlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Done;
        ulong gasLimit = GasCostOf.Transaction + GasCostOf.VeryLow + GasCostOf.BlockHash + Eip8038Constants.ColdStorageAccess - 1;

        CallOutputTracer tracer = ExecuteCode(code, gasLimit);

        Assert.That(tracer.Error, Is.EqualTo(EvmExceptionType.OutOfGas.ToString()));
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

    private CallOutputTracer ExecuteCode(byte[] code, ulong gasLimit = 100_000)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        CallOutputTracer tracer = new();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
        return tracer;
    }

    private void SetHistoryHash(ulong blockNumber, Hash256 hash)
    {
        UInt256 storageIndex = new(blockNumber % Eip7709Spec.Eip2935RingBufferSize);
        StorageCell storageCell = new(Eip2935Constants.BlockHashHistoryAddress, storageIndex);
        TestState.Set(storageCell, hash.Bytes.ToArray());
    }
}
