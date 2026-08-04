// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8037.md
public static class Eip8037Constants
{
    /// <summary> Execution gas component of the EIP-7702 auth-base cost under the two-dimensional gas model. </summary>
    /// <remarks> Spelled <c>REGULAR_PER_AUTH_BASE_COST</c> in EIP-8037; renamed here for consistency with the execution-gas dimension. </remarks>
    public const ulong PerAuthBaseExecutionCost = 7_500;

    /// <summary> State bytes charged per authorization tuple for the state-gas dimension. </summary>
    public const long StateBytesPerAuthBase = 23;

    public const ulong SystemCallBaseGasLimit = 30_000_000;
    public const long SystemMaxSstoresPerCall = 16;
    public const long SystemCallStateReservoir = GasCostOf.StateBytesPerStorageSet * GasCostOf.CostPerStateByte * SystemMaxSstoresPerCall;
    public const ulong SystemCallGasLimit = SystemCallBaseGasLimit + SystemCallStateReservoir;
}
