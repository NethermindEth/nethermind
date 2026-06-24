// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// EIP-8038 state-access gas cost parameters, layered on top of the EIP-8037 two-dimensional
/// (regular + state) gas model.
/// </summary>
/// <remarks>
/// The EIP is a Draft: the final repriced values are still <c>TBD</c>. The base parameters below are
/// therefore placeholders equal to the current (pre-8038) costs, so that enabling the EIP is a no-op
/// for the base constants until the final figures land. The derived parameters are expressed via the
/// EIP's derivation formulas so they recompute automatically once the base values are finalized.
/// The EIP's full parameter set is defined here to track the spec and pin the derivation formulas
/// (see <c>Eip8038ConstantsTests</c>); some parameters (e.g. <see cref="CreateAccess"/>,
/// <see cref="CallValue"/>, <see cref="StorageWrite"/>, <see cref="StorageClearRefund"/>) are not yet
/// referenced by the charging code and will be wired in alongside the remaining repricing once the EIP
/// is finalized.
/// </remarks>
public static class Eip8038Constants
{
    // Base parameters (placeholders == current values; final values TBD).

    /// <summary>Cold account-touch cost (EIP-2929 <c>COLD_ACCOUNT_ACCESS</c>).</summary>
    public const long ColdAccountAccess = GasCostOf.ColdAccountAccess; // 2600

    /// <summary>Warm state-access cost (EIP-2929 <c>WARM_ACCESS</c>).</summary>
    public const long WarmAccess = GasCostOf.WarmStateRead; // 100

    /// <summary>Cold storage-slot access cost (EIP-2929 <c>COLD_STORAGE_ACCESS</c>).</summary>
    public const long ColdStorageAccess = GasCostOf.ColdSLoad; // 2100

    /// <summary>The account-write component of value-bearing <c>*CALL</c>s (<c>CALL_VALUE - CALL_STIPEND</c>).</summary>
    public const long AccountWrite = GasCostOf.CallValue - GasCostOf.CallStipend; // 6700

    /// <summary>The regular-gas write component of <c>SSTORE</c> (<c>GAS_STORAGE_UPDATE - COLD_STORAGE_ACCESS - WARM_ACCESS</c>).</summary>
    public const long StorageWrite = GasCostOf.SReset - ColdStorageAccess - WarmAccess; // 2800

    /// <summary>Stipend forwarded with a value-bearing call (unchanged from EIP-2929).</summary>
    public const long CallStipend = GasCostOf.CallStipend; // 2300

    // Derived parameters (EIP-8038 derivation formulas; recompute from the base values above).

    /// <summary><c>CALL_VALUE = ACCOUNT_WRITE + CALL_STIPEND</c>.</summary>
    public const long CallValue = AccountWrite + CallStipend; // 9000

    /// <summary><c>CREATE_ACCESS = ACCOUNT_WRITE + COLD_STORAGE_ACCESS</c>, charged in regular gas by CREATE/CREATE2.</summary>
    public const long CreateAccess = AccountWrite + ColdStorageAccess;

    /// <summary>Access-list address entry cost, redefined to <c>COLD_ACCOUNT_ACCESS</c>.</summary>
    public const long AccessListAddressCost = ColdAccountAccess; // 2600 (was 2400)

    /// <summary>Access-list storage-key entry cost, redefined to <c>COLD_STORAGE_ACCESS</c>.</summary>
    public const long AccessListStorageKeyCost = ColdStorageAccess; // 2100 (was 1900)

    /// <summary><c>STORAGE_CLEAR_REFUND = (STORAGE_WRITE + COLD_STORAGE_ACCESS) * 4800 / 5000</c>.</summary>
    public const long StorageClearRefund = (StorageWrite + ColdStorageAccess) * 4800 / 5000;
}
