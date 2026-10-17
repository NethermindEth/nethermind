// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Eez.Execution;

public static class EezConstants
{
    /// <summary>The EIP-2718 type of EEZ's unsigned native system transactions.</summary>
    public const TxType SystemTxType = (TxType)0x76;

    /// <summary>The execution budget every rollup0 system transaction receives; the wire format carries no gas fields.</summary>
    public const ulong SystemTxGasLimit = 2_000_000;

    /// <summary>The codeless sender of every system transaction, distinct from the Ethereum system caller.</summary>
    public static readonly Address SystemAddress = new("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee0076");

    /// <summary>The EEZ rollup id of Ethereum itself, the source of inbound and the target of outbound calls.</summary>
    public const ulong L1RollupId = 0;

    /// <summary>rollup0's immutable L2 execution manager predeploy, the only permitted target of its system transactions.</summary>
    public static readonly Address Eezl2Address = new("0x4200000000000000000000000000000000000007");

    /// <summary>RollupX's upgradeable L2 execution manager proxy, the only permitted target of its system transactions.</summary>
    public static readonly Address RollupXEezl2Address = new("0x4B2760F84B83d6b3829E30A7573B0172B6d6402C");
}
