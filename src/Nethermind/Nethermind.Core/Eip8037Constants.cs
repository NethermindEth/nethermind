// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip8037Constants
{
    public const ulong SystemCallBaseGasLimit = 30_000_000;
    public const ulong SystemMaxSstoresPerCall = 16;
    public const ulong SystemCallStateReservoir = GasCostOf.StateBytesPerStorageSet * GasCostOf.CostPerStateByte * SystemMaxSstoresPerCall;
    public const ulong SystemCallGasLimit = SystemCallBaseGasLimit + SystemCallStateReservoir;
}
