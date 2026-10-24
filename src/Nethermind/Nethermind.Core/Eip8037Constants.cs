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

    /// <summary> Absolute cap on <c>tx.gas</c> covering both gas dimensions (2^32 - 1). </summary>
    public const ulong TxMaxTotalGasLimit = 4_294_967_295;

    /// <summary> Legacy share of a new account's state gas: paid from <c>gas_left</c> before the reservoir. </summary>
    /// <remarks> Keeps the <c>gas_left</c> cost of account creation at least its cost before EIP-8037. </remarks>
    public const long NewAccountLegacyShare = (long)GasCostOf.NewAccount;

    /// <summary> Legacy share of a CREATE's account state gas: paid from <c>gas_left</c> before the reservoir. </summary>
    /// <remarks> <c>max(0, GAS_CREATE - CREATE_ACCESS)</c>: keeps the <c>gas_left</c> cost of CREATE at least its cost before EIP-8037. Assumes EIP-8038 (<c>CREATE_ACCESS</c>), which Amsterdam enables together with EIP-8037. </remarks>
    public const long CreateLegacyShare = GasCostOf.Create > Eip8038Constants.CreateAccess ? (long)(GasCostOf.Create - Eip8038Constants.CreateAccess) : 0;

    public const ulong SystemCallBaseGasLimit = 30_000_000;
    public const long SystemMaxSstoresPerCall = 16;
    public const long SystemCallStateReservoir = GasCostOf.StateBytesPerStorageSet * GasCostOf.CostPerStateByte * SystemMaxSstoresPerCall;
    public const ulong SystemCallGasLimit = SystemCallBaseGasLimit + SystemCallStateReservoir;
}
