// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc.RPC;

/// <summary>Amount minted before the reward upgrade, in Wei.</summary>
public class XdcSupplyV1
{
    public UInt256 Minted { get; set; }
}

/// <summary>Amounts minted and burned since the reward upgrade, in Wei.</summary>
public class XdcSupplyV2
{
    public UInt256 Minted { get; set; }
    public UInt256 Burned { get; set; }
}

/// <summary>Token supply accounting as of one epoch.</summary>
public class XdcTokenSupply
{
    public XdcSupplyV1? V1 { get; set; }
    public XdcSupplyV2? V2 { get; set; }

    /// <summary>Total minted across both reward regimes, in Wei.</summary>
    public UInt256 Minted { get; set; }

    /// <summary>First epoch for which the reward upgrade recorded accounting.</summary>
    public UInt256 UpgradeEpochNum { get; set; }

    /// <summary>Epoch the figures describe.</summary>
    public UInt256 EpochNum { get; set; }

    /// <summary>Block at which the epoch's rewards were paid out.</summary>
    public Hash256? BlockHash { get; set; }

    /// <inheritdoc cref="BlockHash"/>
    public UInt256 BlockNumber { get; set; }
}
