// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class SStorePricingKernelTests
{
    private const ulong ColdStorageAccessGas = 2_100;
    private const ulong WarmAccessGas = 100;
    private const ulong StorageWriteGas = 10_000;
    private const long StorageClearRefund = 11_616;
    private const long StorageSetStateGas = 97_920;

    [TestCaseSource(nameof(SStoreCases))]
    public void Price_matches_the_eip8038_sstore_case_table(
        bool isCold,
        bool originalIsZero,
        bool currentIsZero,
        bool newIsZero,
        bool currentSameAsOriginal,
        bool newSameAsCurrent,
        bool newSameAsOriginal,
        ulong expectedAccessGas,
        ulong expectedExecutionWriteGas,
        long expectedStorageClearRefund,
        long expectedStorageClearRefundReversal,
        long expectedRestoreOriginalRefund,
        long expectedStateGasCharge,
        long expectedStateGasRefund)
    {
        SStorePricingInput input = new(
            originalIsZero,
            currentIsZero,
            newIsZero,
            currentSameAsOriginal,
            newSameAsCurrent,
            newSameAsOriginal);
        SStoreAccessStatus accessStatus = isCold ? SStoreAccessStatus.Cold : SStoreAccessStatus.Warm;
        SStoreAccessPricingSchedule accessSchedule = new(
            ColdStorageAccessGas,
            WarmAccessGas);
        SStorePostAccessPricingSchedule postAccessSchedule = new(
            StorageWriteGas,
            StorageClearRefund,
            StorageSetStateGas);

        SStorePricingResult actual = SStorePricingKernel.Price(input, accessStatus, accessSchedule, postAccessSchedule);

        Assert.That(
            (
                actual.AccessGas,
                actual.PostAccess.ExecutionWriteGas,
                actual.PostAccess.StorageClearRefund,
                actual.PostAccess.StorageClearRefundReversal,
                actual.PostAccess.RestoreOriginalRefund,
                actual.PostAccess.StateGasCharge,
                actual.PostAccess.StateGasRefund),
            Is.EqualTo(
                (
                    expectedAccessGas,
                    expectedExecutionWriteGas,
                    expectedStorageClearRefund,
                    expectedStorageClearRefundReversal,
                    expectedRestoreOriginalRefund,
                    expectedStateGasCharge,
                    expectedStateGasRefund)));
    }

    [Test]
    public void Restore_cleared_keeps_the_refund_reversal_and_restore_components_separate()
    {
        SStorePricingInput input = new(
            false,
            true,
            false,
            false,
            false,
            true);
        SStoreAccessPricingSchedule accessSchedule = new(ColdStorageAccessGas, WarmAccessGas);
        SStorePostAccessPricingSchedule postAccessSchedule = new(
            StorageWriteGas,
            StorageClearRefund,
            StorageSetStateGas);

        SStorePricingResult actual = SStorePricingKernel.Price(
            input,
            SStoreAccessStatus.Warm,
            accessSchedule,
            postAccessSchedule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.PostAccess.StorageClearRefundReversal, Is.EqualTo(-StorageClearRefund));
            Assert.That(actual.PostAccess.RestoreOriginalRefund, Is.EqualTo((long)StorageWriteGas));
            Assert.That(actual.PostAccess.StorageClearRefundReversal + actual.PostAccess.RestoreOriginalRefund, Is.EqualTo(-1_616));
        }
    }

    [Test]
    public void Restore_original_refund_uses_the_explicit_unchecked_uint64_to_int64_conversion()
    {
        SStorePricingInput input = new(
            false,
            false,
            false,
            false,
            false,
            true);
        SStoreAccessPricingSchedule accessSchedule = new(ColdStorageAccessGas, WarmAccessGas);
        SStorePostAccessPricingSchedule postAccessSchedule = new(
            ulong.MaxValue,
            StorageClearRefund,
            StorageSetStateGas);

        SStorePricingResult actual = SStorePricingKernel.Price(
            input,
            SStoreAccessStatus.Warm,
            accessSchedule,
            postAccessSchedule);

        Assert.That(actual.PostAccess.RestoreOriginalRefund, Is.EqualTo(-1L));
    }

    [TestCase(StorageWriteGas - 1, false)]
    [TestCase(StorageWriteGas, true)]
    public void New_slot_charges_execution_before_state_gas_at_the_out_of_gas_boundary(
        ulong availableExecutionGas,
        bool expectedSuccess)
    {
        SStorePricingInput input = new(
            true,
            true,
            false,
            true,
            false,
            false);
        SStorePostAccessPricingResult pricing = SStorePricingKernel.PriceAfterAccess(
            input,
            new SStorePostAccessPricingSchedule(
                StorageWriteGas,
                StorageClearRefund,
                StorageSetStateGas));
        EthereumGasPolicy gas = new()
        {
            Value = availableExecutionGas,
            StateReservoir = StorageSetStateGas,
            StateGasUsed = 7,
            StateGasSpill = 11,
            StateGasSpillRefunded = 13,
        };

        bool actual = EthereumGasPolicy.TryConsumeStateAndExecutionGas(
            ref gas,
            pricing.StateGasCharge,
            pricing.ExecutionWriteGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Is.EqualTo(expectedSuccess));
            Assert.That(gas.Value, Is.EqualTo(0));
            Assert.That(gas.StateReservoir, Is.EqualTo(expectedSuccess ? 0 : StorageSetStateGas));
            Assert.That(gas.StateGasUsed, Is.EqualTo(expectedSuccess ? 7 + StorageSetStateGas : 7));
            Assert.That(gas.StateGasSpill, Is.EqualTo(11));
            Assert.That(gas.StateGasSpillRefunded, Is.EqualTo(13));
        }
    }

    private static IEnumerable<TestCaseData> SStoreCases
    {
        get
        {
            foreach (SStoreSemanticCase semanticCase in SemanticCases)
            {
                yield return CreateCase(semanticCase, true, ColdStorageAccessGas);
                yield return CreateCase(semanticCase, false, WarmAccessGas);
            }
        }
    }

    private static TestCaseData CreateCase(SStoreSemanticCase semanticCase, bool isCold, ulong expectedAccessGas) =>
        new TestCaseData(
                isCold,
                semanticCase.OriginalIsZero,
                semanticCase.CurrentIsZero,
                semanticCase.NewIsZero,
                semanticCase.CurrentSameAsOriginal,
                semanticCase.NewSameAsCurrent,
                semanticCase.NewSameAsOriginal,
                expectedAccessGas,
                semanticCase.ExecutionWriteGas,
                semanticCase.StorageClearRefund,
                semanticCase.StorageClearRefundReversal,
                semanticCase.RestoreOriginalRefund,
                semanticCase.StateGasCharge,
                semanticCase.StateGasRefund)
            .SetName($"{semanticCase.Name}_{(isCold ? SStoreAccessStatus.Cold : SStoreAccessStatus.Warm)}");

    private static readonly SStoreSemanticCase[] SemanticCases =
    [
        new("new_slot", true, true, false, true, false, false, StorageWriteGas, 0, 0, 0, StorageSetStateGas, 0),
        new("first_update", false, false, false, true, false, false, StorageWriteGas, 0, 0, 0, 0, 0),
        new("first_clear", false, false, true, true, false, false, StorageWriteGas, StorageClearRefund, 0, 0, 0, 0),
        new("reset_created", true, false, true, false, false, true, 0, 0, 0, (long)StorageWriteGas, 0, StorageSetStateGas),
        new("rewrite_created", true, false, false, false, false, false, 0, 0, 0, 0, 0, 0),
        new("restore_dirty", false, false, false, false, false, true, 0, 0, 0, (long)StorageWriteGas, 0, 0),
        new("clear_dirty", false, false, true, false, false, false, 0, StorageClearRefund, 0, 0, 0, 0),
        new("restore_cleared", false, true, false, false, false, true, 0, 0, -StorageClearRefund, (long)StorageWriteGas, 0, 0),
        new("rewrite_cleared", false, true, false, false, false, false, 0, 0, -StorageClearRefund, 0, 0, 0),
        new("rewrite_dirty", false, false, false, false, false, false, 0, 0, 0, 0, 0, 0),
        new("no_op", false, false, false, true, true, true, 0, 0, 0, 0, 0, 0),
    ];

    private readonly record struct SStoreSemanticCase(
        string Name,
        bool OriginalIsZero,
        bool CurrentIsZero,
        bool NewIsZero,
        bool CurrentSameAsOriginal,
        bool NewSameAsCurrent,
        bool NewSameAsOriginal,
        ulong ExecutionWriteGas,
        long StorageClearRefund,
        long StorageClearRefundReversal,
        long RestoreOriginalRefund,
        long StateGasCharge,
        long StateGasRefund);
}
