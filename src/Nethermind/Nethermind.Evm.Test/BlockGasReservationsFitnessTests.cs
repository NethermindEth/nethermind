// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Pins the single-source contract of <see cref="Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations"/>:
/// block-production admission and end-of-block inclusion validation read block gas reservations from the same
/// helper, so the two paths reserve identical execution and state gas for the same transaction and cannot diverge.
/// </summary>
[TestFixture]
public class BlockGasReservationsFitnessTests
{
    private static readonly IReleaseSpec Eip8037Spec = Eip8141Prototype.Instance;
    private static readonly IReleaseSpec PreEip8037Spec = Amsterdam.NoEip8037Instance;

    private const ulong SenderFrameStateGas = 150_000;

    private static Transaction NonFrameTx(ulong gasLimit) =>
        new()
        {
            Type = TxType.EIP1559,
            GasLimit = gasLimit,
            SenderAddress = TestItem.AddressA,
        };

    private static Transaction FrameTx() =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = TestItem.AddressA,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
                new TxFrame(TxFrame.ModeSender, 0, TestItem.AddressB, executionGasLimit: 300_000, stateGasLimit: SenderFrameStateGas, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private static IEnumerable<TestCaseData> NonFrameCases()
    {
        yield return new TestCaseData(NonFrameTx(1_000_000), Eip8037Spec, 1_000_000UL, 1_000_000UL)
            .SetName("NonFrame_under_eip8037_reserves_full_gas_in_both_dimensions");
        yield return new TestCaseData(NonFrameTx(Eip7825Constants.DefaultTxGasLimitCap * 3), Eip8037Spec, Eip7825Constants.DefaultTxGasLimitCap, Eip7825Constants.DefaultTxGasLimitCap * 3)
            .SetName("NonFrame_under_eip8037_caps_execution_reservation_at_tx_gas_limit_cap");
        yield return new TestCaseData(NonFrameTx(1_000_000), PreEip8037Spec, 1_000_000UL, 0UL)
            .SetName("NonFrame_without_eip8037_reserves_no_state_gas");
    }

    [TestCaseSource(nameof(NonFrameCases))]
    public void Admission_and_validation_reserve_identical_gas(Transaction tx, IReleaseSpec spec, ulong expectedExecution, ulong expectedState)
    {
        bool admissionComputed = Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong admissionExecution, out ulong admissionState);
        bool validationComputed = Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, spec, out ulong validationExecution, out ulong validationState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(admissionComputed, Is.True);
            Assert.That(validationComputed, Is.EqualTo(admissionComputed));
            Assert.That(validationExecution, Is.EqualTo(admissionExecution));
            Assert.That(validationState, Is.EqualTo(admissionState));
            Assert.That(admissionExecution, Is.EqualTo(expectedExecution));
            Assert.That(admissionState, Is.EqualTo(expectedState));
        }
    }

    [Test]
    public void Admission_and_validation_delegate_frame_reservations_to_frame_validation()
    {
        Transaction tx = FrameTx();

        bool sharedComputed = Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(tx, Eip8037Spec, out ulong sharedExecution, out ulong sharedState);
        bool frameComputed = FrameTxValidation.TryCalculateBlockGasReservations(tx, Eip8037Spec, out ulong frameExecution, out ulong frameState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sharedComputed, Is.True);
            Assert.That(frameComputed, Is.EqualTo(sharedComputed));
            Assert.That(sharedExecution, Is.EqualTo(frameExecution));
            Assert.That(sharedState, Is.EqualTo(frameState));
            Assert.That(sharedState, Is.EqualTo(SenderFrameStateGas));
        }
    }
}
