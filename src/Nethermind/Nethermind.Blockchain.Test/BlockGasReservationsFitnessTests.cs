// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Drives both real consumers of the 2D block gas reservation split — block production admission
/// (<see cref="BlockProcessor.BlockProductionTransactionPicker.CanAddTransaction(Block, Transaction, IReadOnlySet{Transaction}, IReadOnlyStateProvider, ulong, ulong)"/>)
/// and end-of-block inclusion validation (<see cref="BlockAccessListManager.CheckPerTxInclusion"/>) — and pins that
/// they admit and reject the same transaction at the same per-dimension boundary, the invariant the shared helper protects.
/// </summary>
[TestFixture]
public class BlockGasReservationsFitnessTests
{
    private const ulong Nonce = 5;
    private const ulong BlockGasLimit = 30_000_000;
    private static readonly IReleaseSpec Spec = Eip8141Prototype.Instance;

    private static Transaction NonFrameTx() =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithSenderAddress(TestItem.AddressA)
            .WithNonce(Nonce)
            .WithGasLimit(200_000)
            .WithMaxFeePerGas(1)
            .WithMaxPriorityFeePerGas(1)
            .TestObject;

    private static Transaction FrameTx(ulong executionGasLimit, ulong stateGasLimit) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            Nonce = Nonce,
            SenderAddress = TestItem.AddressA,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, executionGasLimit, stateGasLimit, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private static IEnumerable<TestCaseData> Transactions()
    {
        yield return new TestCaseData(NonFrameTx()).SetName("Ordinary transaction under EIP-8037");
        yield return new TestCaseData(FrameTx(executionGasLimit: 500_000, stateGasLimit: 300_000)).SetName("Frame transaction with state gas");
        yield return new TestCaseData(FrameTx(executionGasLimit: 500_000, stateGasLimit: 0)).SetName("Frame transaction without state gas");
    }

    [TestCaseSource(nameof(Transactions))]
    public void Admission_and_validation_agree_at_every_dimension_boundary(Transaction tx)
    {
        Assert.That(Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, Spec, out ulong execution, out ulong state), Is.True);

        AssertPathsAgree(tx, BlockGasLimit - execution, 0);
        AssertPathsAgree(tx, BlockGasLimit - execution + 1, 0);

        if (state > 0)
        {
            AssertPathsAgree(tx, 0, BlockGasLimit - state);
            AssertPathsAgree(tx, 0, BlockGasLimit - state + 1);
        }
    }

    private static void AssertPathsAgree(Transaction tx, ulong cumulativeExecution, ulong cumulativeState)
    {
        ISpecProvider specProvider = new TestSingleReleaseSpecProvider(Spec);
        BlockProcessor.BlockProductionTransactionPicker picker = new(specProvider, BlocksConfig.DefaultMaxTxKilobytes);

        IReadOnlyStateProvider state = Substitute.For<IReadOnlyStateProvider>();
        state.GetNonce(TestItem.AddressA).Returns(Nonce);
        state.GetBalance(TestItem.AddressA).Returns(UInt256.MaxValue);

        Block block = Build.A.Block.WithGasLimit((long)BlockGasLimit).WithBaseFeePerGas(0).TestObject;

        BlockProcessor.AddingTxEventArgs args = picker.CanAddTransaction(
            block, tx, new HashSet<Transaction>(), state, cumulativeExecution, cumulativeState);
        bool admissionAdmits = args.Action == BlockProcessor.TxAction.Add;

        bool validationAdmits;
        try
        {
            BlockAccessListManager.CheckPerTxInclusion(block, 0, tx, Spec, cumulativeExecution, cumulativeState);
            validationAdmits = true;
        }
        catch (InvalidBlockException)
        {
            validationAdmits = false;
        }

        Assert.That(admissionAdmits, Is.EqualTo(validationAdmits),
            $"admission and validation disagreed at cumulativeExecution={cumulativeExecution}, cumulativeState={cumulativeState} (admission reason: {args.Reason})");
    }
}
