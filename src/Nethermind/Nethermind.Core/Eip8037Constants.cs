// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip8037Constants
{
    public const long SystemCallBaseGasLimit = 30_000_000L;
    public const long SystemMaxSstoresPerCall = 16;
    public const long SystemCallStateReservoir = GasCostOf.StateBytesPerStorageSet * GasCostOf.CostPerStateByte * SystemMaxSstoresPerCall;
    public const long SystemCallGasLimit = SystemCallBaseGasLimit + SystemCallStateReservoir;
}
