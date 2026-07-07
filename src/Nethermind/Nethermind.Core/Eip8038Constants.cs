// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip8038Constants
{
    public const ulong ColdAccountAccess = 3000;
    public const ulong WarmAccess = GasCostOf.WarmStateRead;
    public const ulong ColdStorageAccess = 3000;
    public const ulong AccountWrite = 8000;
    public const ulong StorageWrite = 10000;
    public const ulong CallStipend = GasCostOf.CallStipend;

    public const ulong CallValue = AccountWrite + CallStipend;
    public const ulong CreateAccess = AccountWrite + ColdStorageAccess;
    public const ulong AccessListAddressCost = ColdAccountAccess;
    public const ulong AccessListStorageKeyCost = ColdStorageAccess;
    public const ulong StorageClearRefund = (StorageWrite + ColdStorageAccess) * 4800 / 5000;
    public const ulong PerAuthBaseRegular = AccountWrite + (101 * 16 + 3000 + ColdAccountAccess + 2 * WarmAccess);
}
