// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Evm.GasPolicy;

internal enum SStoreAccessStatus : byte
{
    Cold,
    Warm,
}

/// <summary>Canonical storage predicates consumed by the EIP-8038 SSTORE pricing kernel.</summary>
/// <remarks>
/// The opcode adapter derives these predicates from the original, current, and proposed storage
/// values. The kernel intentionally does not read world state or mutate gas, refunds, or tracers.
/// </remarks>
internal readonly struct SStorePricingInput(
    bool originalIsZero,
    bool currentIsZero,
    bool newIsZero,
    bool currentSameAsOriginal,
    bool newSameAsCurrent,
    bool newSameAsOriginal)
{
    public readonly bool OriginalIsZero = originalIsZero;
    public readonly bool CurrentIsZero = currentIsZero;
    public readonly bool NewIsZero = newIsZero;
    public readonly bool CurrentSameAsOriginal = currentSameAsOriginal;
    public readonly bool NewSameAsCurrent = newSameAsCurrent;
    public readonly bool NewSameAsOriginal = newSameAsOriginal;
}

/// <summary>Explicit access-schedule inputs for the pure EIP-8038 SSTORE reference decision.</summary>
internal readonly struct SStoreAccessPricingSchedule(ulong coldStorageAccessGas, ulong warmAccessGas)
{
    public readonly ulong ColdStorageAccessGas = coldStorageAccessGas;
    public readonly ulong WarmAccessGas = warmAccessGas;
}

/// <summary>Explicit post-access schedule inputs for the EIP-8038 SSTORE pricing decision.</summary>
/// <remarks>
/// <see cref="StorageWriteGas"/> is converted to a signed refund component with an unchecked
/// cast. Production schedules keep it at most <see cref="long.MaxValue"/>; the refinement states
/// that no-wrap assumption explicitly.
/// </remarks>
internal readonly struct SStorePostAccessPricingSchedule(
    ulong storageWriteGas,
    long storageClearRefund,
    long storageSetStateGas)
{
    public readonly ulong StorageWriteGas = storageWriteGas;
    public readonly long StorageClearRefund = storageClearRefund;
    public readonly long StorageSetStateGas = storageSetStateGas;
}

/// <summary>Independent post-access gas and refund components selected for one SSTORE decision.</summary>
/// <remarks>
/// Refund components remain separate so the opcode adapter can preserve their individual tracer
/// events and the state-gas refill position.
/// </remarks>
internal readonly struct SStorePostAccessPricingResult(
    ulong executionWriteGas,
    long storageClearRefund,
    long storageClearRefundReversal,
    long restoreOriginalRefund,
    long stateGasCharge,
    long stateGasRefund)
{
    public readonly ulong ExecutionWriteGas = executionWriteGas;
    public readonly long StorageClearRefund = storageClearRefund;
    public readonly long StorageClearRefundReversal = storageClearRefundReversal;
    public readonly long RestoreOriginalRefund = restoreOriginalRefund;
    public readonly long StateGasCharge = stateGasCharge;
    public readonly long StateGasRefund = stateGasRefund;
}

/// <summary>Reference-only full pure SSTORE pricing result, including the access component.</summary>
/// <remarks>
/// The opcode uses <see cref="SStorePricingKernel.PriceAfterAccess"/> after the independently
/// metered access-policy boundary; this result is retained for testing and formal reference
/// composition without another access-tracker lookup.
/// </remarks>
internal readonly struct SStorePricingResult(
    ulong accessGas,
    SStorePostAccessPricingResult postAccess)
{
    public readonly ulong AccessGas = accessGas;
    public readonly SStorePostAccessPricingResult PostAccess = postAccess;
}

/// <summary>Allocation-free value kernel for EIP-8038 SSTORE pricing and state-gas effects.</summary>
internal static class SStorePricingKernel
{
    /// <summary>Calculates the production-used EIP-8038 SSTORE effects after storage access is metered.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SStorePostAccessPricingResult PriceAfterAccess(
        SStorePricingInput input,
        SStorePostAccessPricingSchedule schedule)
    {
        bool writesFirstValue = !input.NewSameAsCurrent && input.CurrentSameAsOriginal;
        long restoreOriginalRefund = input.NewSameAsOriginal && !input.NewSameAsCurrent
            ? unchecked((long)schedule.StorageWriteGas)
            : 0;

        return new SStorePostAccessPricingResult(
            writesFirstValue ? schedule.StorageWriteGas : 0,
            !input.OriginalIsZero && !input.CurrentIsZero && input.NewIsZero ? schedule.StorageClearRefund : 0,
            !input.OriginalIsZero && input.CurrentIsZero && !input.NewIsZero ? unchecked(-schedule.StorageClearRefund) : 0,
            restoreOriginalRefund,
            input.OriginalIsZero && input.CurrentIsZero && !input.NewIsZero ? schedule.StorageSetStateGas : 0,
            input.OriginalIsZero && !input.CurrentIsZero && input.NewIsZero ? schedule.StorageSetStateGas : 0);
    }

    /// <summary>Composes an explicit access schedule with the post-access reference decision.</summary>
    /// <remarks>
    /// This reference wrapper is not used by <c>InstructionSStoreMetered</c>, whose access policy
    /// owns warming, tracing-access behavior, and access OutOfGas ordering.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SStorePricingResult Price(
        SStorePricingInput input,
        SStoreAccessStatus accessStatus,
        SStoreAccessPricingSchedule accessSchedule,
        SStorePostAccessPricingSchedule postAccessSchedule)
    {
        ulong accessGas = accessStatus == SStoreAccessStatus.Cold
            ? accessSchedule.ColdStorageAccessGas
            : accessSchedule.WarmAccessGas;
        SStorePostAccessPricingResult postAccess = PriceAfterAccess(input, postAccessSchedule);
        return new SStorePricingResult(accessGas, postAccess);
    }
}
