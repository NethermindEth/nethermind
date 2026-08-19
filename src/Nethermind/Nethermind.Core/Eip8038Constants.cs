// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip8038Constants
{
    public const ulong ColdAccountAccess = 3000;
    public const ulong WarmAccess = GasCostOf.WarmStateRead;
    public const ulong ColdStorageAccess = 2100;
    public const ulong AccountWrite = 9000;
    public const ulong StorageWrite = 10000;
    public const ulong CallStipend = GasCostOf.CallStipend;

    public const ulong CallValue = AccountWrite + CallStipend;
    public const ulong CreateAccess = AccountWrite + ColdAccountAccess;

    // Pre-warming an access-list entry buys the cold access minus the warm access still paid on use.
    public const ulong AccessListAddressCost = ColdAccountAccess - WarmAccess;
    public const ulong AccessListStorageKeyCost = ColdStorageAccess - WarmAccess;

    public const ulong PerAuthBaseExecution = AuthTupleCalldataCost + EcRecoverCost + ColdAccountAccess + 2 * WarmAccess;

    private const ulong AuthTupleCalldataCost = 101 * GasCostOf.TxDataNonZeroEip2028;
    private const ulong EcRecoverCost = 3000;
}
