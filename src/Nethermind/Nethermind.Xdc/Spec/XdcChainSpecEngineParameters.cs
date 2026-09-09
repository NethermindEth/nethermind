// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc.Spec;

public class XdcChainSpecEngineParameters : IChainSpecEngineParameters
{
    public virtual string EngineName => SealEngineType;
    public virtual string SealEngineType => XdcConstants.XDPoS;
    public ulong Epoch { get; set; }
    public ulong Gap { get; set; }
    public Address FoundationWalletAddr { get; set; }
    public ulong Reward { get; set; }
    public ulong SwitchEpoch { get; set; }
    public ulong SwitchBlock { get; set; }
    public ulong RangeReturnSigner { get; set; }
    public Address[] GenesisMasternodes { get; set; } = Array.Empty<Address>();

    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }

    public Address MasternodeVotingContract { get; set; }

    private List<V2ConfigParams> _v2Configs = [];
    public List<V2ConfigParams> V2Configs
    {
        get => _v2Configs;
        set
        {
            Span<V2ConfigParams> v2Configs = CollectionsMarshal.AsSpan(value);
            v2Configs.Sort(default(V2ConfigBySwitchRoundComparer));
            CheckConfig(v2Configs);
            _v2Configs = value;
        }
    }

    public ulong? TipTrc21Fee { get; set; }
    public ulong TIP2019Block { get; set; }

    // These fork blocks are only required for fork ID calculation, and are not actually supported
    public ulong? TipSigningBlock { get; set; }
    public ulong? TipRandomizeBlock { get; set; }
    public ulong? TipIncreaseMasternodesBlock { get; set; }
    public ulong? TipNoHalvingMNRewardBlock { get; set; }
    public ulong? TipXDCXLendingBlock { get; set; }
    public ulong? TipXDCXCancellationFeeBlock { get; set; }
    public ulong? Gas50xBlock { get; set; }

    /// <summary>
    /// Gas price floor, in wei, that the transaction pool applies before <see cref="Gas50xBlock"/>; from that block it
    /// is multiplied by <see cref="XdcConstants.Gas50xMultiplier"/>. Defaults to
    /// <see cref="XdcConstants.DefaultMinGasPrice"/> (0.25 gwei, so 12.5 gwei once raised).
    /// </summary>
    /// <remarks>
    /// Mirrors the reference client's <c>--miner-gasprice</c>: a value below the default is raised to it, so the floor
    /// can never be weakened below what the network expects. Zero means the same as unset here; only a subnet can
    /// disable the check, via <see cref="XdcSubnetChainSpecEngineParameters"/>.
    /// </remarks>
    public UInt256? MinGasPrice { get; set; }

    public ulong? TipUpgradePenalty { get; set; }
    public ulong? TipUpgradeReward { get; set; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 MasternodeReward { get; set; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 ProtectorReward { get; set; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 ObserverReward { get; set; }
    public ulong MergeSignRange { get; set; }
    public Address[] BlackListedAddresses { get; set; }
    public ulong? BlackListHFNumber { get; set; }
    public ulong? TipXDCX { get; set; }
    public ulong? TIPXDCXMinerDisable { get; set; }
    public ulong? TIPXDCXReceiverDisable { get; set; }
    public ulong? DynamicGasLimitBlock { get; set; }

    /// <summary>The transaction pool's gas price floor, in wei, at <paramref name="blockNumber"/>.</summary>
    /// <remarks>Mirrors <c>common.GetMinGasPrice</c> of XDPoSChain: the configured floor below
    /// <see cref="Gas50xBlock"/>, raised 50x from it.</remarks>
    internal virtual UInt256 ResolveMinGasPrice(ulong blockNumber) =>
        (Gas50xBlock ?? ulong.MaxValue) <= blockNumber
            ? ConfiguredMinGasPrice * XdcConstants.Gas50xMultiplier
            : ConfiguredMinGasPrice;

    /// <summary>The stated floor, never below <see cref="XdcConstants.DefaultMinGasPrice"/>.</summary>
    protected UInt256 ConfiguredMinGasPrice =>
        UInt256.Max(MinGasPrice.GetValueOrDefault(), XdcConstants.DefaultMinGasPrice);

    private readonly struct V2ConfigBySwitchRoundComparer : IComparer<V2ConfigParams>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(V2ConfigParams a, V2ConfigParams b) => a.SwitchRound.CompareTo(b.SwitchRound);
    }

    private static void CheckConfig(ReadOnlySpan<V2ConfigParams> list)
    {
        if (list.Length == 0 || list[0].SwitchRound != 0)
            throw new InvalidOperationException("There should be a default configuration with switchRound = 0");
        for (int i = 1; i < list.Length; i++)
        {
            if (list[i].SwitchRound == list[i - 1].SwitchRound)
                throw new InvalidOperationException($"Duplicate config for round {list[i].SwitchRound}.");
        }
    }

    public void ApplyToReleaseSpec(ReleaseSpec spec, ulong startBlock, ulong? startTimestamp) => spec.BaseFeeCalculator = new XdcBaseFeeCalculator();

    public void AddTransitions(SortedSet<ulong> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (TipTrc21Fee is not null)
            blockNumbers.Add(TipTrc21Fee.Value);
        if (TipUpgradePenalty is not null)
            blockNumbers.Add(TipUpgradePenalty.Value);
        if (TipUpgradeReward is not null)
            blockNumbers.Add(TipUpgradeReward.Value);
        // Without its own release spec boundary the flag would only flip on whichever transition encloses it.
        if (DynamicGasLimitBlock is not null)
            blockNumbers.Add(DynamicGasLimitBlock.Value);
        // The transaction pool's gas price floor changes here.
        if (Gas50xBlock is not null)
            blockNumbers.Add(Gas50xBlock.Value);
        if (TipXDCX is not null)
            blockNumbers.Add(TipXDCX.Value);
        if (TIPXDCXMinerDisable is not null)
            blockNumbers.Add(TIPXDCXMinerDisable.Value);
        if (TIPXDCXReceiverDisable is not null)
            blockNumbers.Add(TIPXDCXReceiverDisable.Value);
    }
}

public sealed class V2ConfigParams
{
    public ulong SwitchRound { get; init; }
    public int MaxMasternodes { get; init; }
    public int MaxProtectorNodes { get; init; }
    public int MaxObserverNodes { get; init; }
    public double CertificateThreshold { get; init; }
    public int TimeoutSyncThreshold { get; init; }
    public int TimeoutPeriod { get; init; }
    public ulong MinePeriod { get; init; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 MasternodeReward { get; init; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 ProtectorReward { get; init; }
    [JsonConverter(typeof(XdcToWeiConverter))]
    public UInt256 ObserverReward { get; init; }
    public ulong MinimumMinerBlockPerEpoch { get; init; }
    public ulong LimitPenaltyEpoch { get; init; }
    public ulong MinimumSigningTx { get; init; }
}
