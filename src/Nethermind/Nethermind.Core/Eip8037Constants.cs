// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

// https://github.com/ethereum/EIPs/blob/master/EIPS/eip-8037.md
public static class Eip8037Constants
{
    /// <summary> Regular gas component of the EIP-7702 auth-base cost under the two-dimensional gas model. </summary>
    public const long PerAuthBaseRegularCost = 7_500;

    /// <summary> State bytes charged per authorization tuple for the state-gas dimension. </summary>
    public const long StateBytesPerAuthBase = 23;

    public const long SystemCallBaseGasLimit = 30_000_000L;
    public const long SystemMaxSstoresPerCall = 16;
    public const long SystemCallStateReservoir = GasCostOf.StateBytesPerStorageSet * GasCostOf.CostPerStateByte * SystemMaxSstoresPerCall;
    public const long SystemCallGasLimit = SystemCallBaseGasLimit + SystemCallStateReservoir;
}
