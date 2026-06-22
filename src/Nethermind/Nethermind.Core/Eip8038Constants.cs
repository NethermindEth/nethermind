// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// EIP-8038 state-access gas cost parameters, layered on top of the EIP-8037 two-dimensional
/// (regular + state) gas model.
/// </summary>
/// <remarks>
/// Scheduled in Amsterdam by glamsterdam-devnet-6 with the final repriced values below. The derived
/// parameters are expressed via the EIP's derivation formulas so they recompute from the base values.
/// </remarks>
public static class Eip8038Constants
{
    // Base parameters (final values per EIP-8038, glamsterdam-devnet-6).

    /// <summary>Cold account-touch cost (<c>COLD_ACCOUNT_ACCESS</c>).</summary>
    public const long ColdAccountAccess = 3000; // was 2600 (EIP-2929)

    /// <summary>Warm state-access cost (<c>WARM_ACCESS</c>).</summary>
    public const long WarmAccess = GasCostOf.WarmStateRead; // 100 (unchanged)

    /// <summary>Cold storage-slot access cost (<c>COLD_STORAGE_ACCESS</c>).</summary>
    public const long ColdStorageAccess = 3000; // was 2100 (EIP-2929)

    /// <summary>The account-write component of value-bearing <c>*CALL</c>s (<c>CALL_VALUE - CALL_STIPEND</c>).</summary>
    public const long AccountWrite = 8000;

    /// <summary>The regular-gas write component of <c>SSTORE</c> (<c>STORAGE_WRITE</c>).</summary>
    public const long StorageWrite = 10000;

    /// <summary>Stipend forwarded with a value-bearing call (unchanged from EIP-2929).</summary>
    public const long CallStipend = GasCostOf.CallStipend; // 2300

    // Derived parameters (EIP-8038 derivation formulas; recompute from the base values above).

    /// <summary><c>CALL_VALUE = ACCOUNT_WRITE + CALL_STIPEND</c>.</summary>
    public const long CallValue = AccountWrite + CallStipend; // 10300

    /// <summary><c>CREATE_ACCESS = ACCOUNT_WRITE + COLD_STORAGE_ACCESS</c>, charged in regular gas by CREATE/CREATE2.</summary>
    public const long CreateAccess = AccountWrite + ColdStorageAccess;

    /// <summary>Access-list address entry cost, redefined to <c>COLD_ACCOUNT_ACCESS</c>.</summary>
    public const long AccessListAddressCost = ColdAccountAccess; // 3000 (was 2400)

    /// <summary>Access-list storage-key entry cost, redefined to <c>COLD_STORAGE_ACCESS</c>.</summary>
    public const long AccessListStorageKeyCost = ColdStorageAccess; // 3000 (was 1900)

    /// <summary><c>STORAGE_CLEAR_REFUND = (STORAGE_WRITE + COLD_STORAGE_ACCESS) * 4800 / 5000</c>.</summary>
    public const long StorageClearRefund = (StorageWrite + ColdStorageAccess) * 4800 / 5000; // 12480
}
