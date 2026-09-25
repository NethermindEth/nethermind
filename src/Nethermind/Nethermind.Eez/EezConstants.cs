// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Eez;

public static class EezConstants
{
    /// <summary>The EIP-2718 type of EEZ's unsigned native system transactions.</summary>
    public const TxType SystemTxType = (TxType)0x76;

    /// <summary>The execution budget every system transaction receives; the wire format carries no gas fields.</summary>
    public const ulong SystemTxGasLimit = 2_000_000;

    /// <summary>The codeless sender of every system transaction, distinct from the Ethereum system caller.</summary>
    public static readonly Address SystemAddress = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee0076");

    /// <summary>The L2 execution manager predeploy, the only permitted target of a system transaction.</summary>
    public static readonly Address Eezl2Address = new("0x4200000000000000000000000000000000000007");
}
