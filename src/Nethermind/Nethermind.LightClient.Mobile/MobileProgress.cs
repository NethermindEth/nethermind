// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;

namespace Nethermind.LightClient.Mobile;

/// <summary>A balance authenticated against one specific mainnet execution state root.</summary>
public sealed record MobileBalance(string Wei, ulong BlockNumber, string BlockHash, string StateRoot,
    DateTimeOffset VerifiedAt, double RequestMilliseconds);

/// <summary>An asset balance and the authenticated execution state it used.</summary>
public sealed record MobileTokenBalance(string Symbol, int Decimals, string? Units = null,
    ulong? BlockNumber = null, string? BlockHash = null, string? StateRoot = null,
    DateTimeOffset? VerifiedAt = null, double? RequestMilliseconds = null, string? Error = null)
{
    /// <summary>The token amount rounded to two decimal places, without a ticker suffix, retaining an explicit unavailable state.</summary>
    public string AmountText => Units is null ? Error is null ? "Waiting for verified state" : "Unavailable"
        : FormatAmount(Units, Decimals);

    /// <summary>The last verified block and any subsequent refresh error.</summary>
    public string DetailsText
    {
        get
        {
            string proof = Units is null ? "" : $"Verified at block {BlockNumber}";
            if (Error is null) return proof;
            return (proof.Length == 0 ? "" : proof + "\n") + $"Refresh unavailable: {Error}";
        }
    }

    private static string FormatAmount(string units, int decimals)
    {
        BigInteger value = BigInteger.Parse(units, CultureInfo.InvariantCulture);
        BigInteger divisor = BigInteger.Pow(10, decimals - 2);
        BigInteger rounded = (value + divisor / 2) / divisor;
        BigInteger whole = BigInteger.DivRem(rounded, 100, out BigInteger fraction);
        return whole.ToString("N0", CultureInfo.InvariantCulture) + "." + fraction.ToString("D2", CultureInfo.InvariantCulture);
    }
}

/// <summary>An immutable view of mobile sync and the last successfully verified balance.</summary>
public sealed record MobileProgress(string Stage, ulong? HeadNumber = null, ulong? HeadSlot = null,
    ulong? FinalizedNumber = null, bool HeadFresh = false, int BeaconPeers = 0, int ExecutionPeers = 0,
    int SnapPeers = 0, MobileBalance? Balance = null, bool BalanceAtHead = false, string? Error = null,
    IReadOnlyList<MobileTokenBalance>? Tokens = null, MobileTrackingSettings? Settings = null)
{
    /// <summary>Whether every selected balance matches a fresh authenticated head without an outstanding error.</summary>
    public bool IsSynced
    {
        get
        {
            if (!HeadFresh || !BalanceAtHead || Balance is null || Error is not null) return false;
            int selected = Settings is null ? 0 : (Settings.Usdt ? 1 : 0) + (Settings.Dai ? 1 : 0) + (Settings.Usdc ? 1 : 0);
            if ((Tokens?.Count ?? 0) != selected) return false;
            if (Tokens is not null)
                foreach (MobileTokenBalance token in Tokens)
                    if (!IsCurrent(token)) return false;
            return true;
        }
    }

    /// <summary>Whether this asset's proof matches the fresh authenticated head without a refresh error.</summary>
    public bool IsCurrent(MobileTokenBalance token) => HeadFresh && BalanceAtHead && Balance is not null && Error is null
        && token.Units is not null && token.Error is null && token.BlockHash == Balance.BlockHash
        && token.StateRoot == Balance.StateRoot && token.BlockNumber == Balance.BlockNumber;

    /// <summary>The current phase, with sync readiness distinguished from waiting for a fresh balance proof.</summary>
    public string StatusText => Stage == "Following mainnet" ? IsSynced ? "Synced" : "Syncing" : Stage;
}
