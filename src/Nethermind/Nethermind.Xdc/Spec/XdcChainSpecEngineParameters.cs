// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc.Spec;

public class XdcChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => XdcConstants.XDPoS;
    public int Epoch { get; set; }
    public int Gap { get; set; }
    public int Period { get; set; }
    public bool SkipV1Validation { get; set; }
    public Address FoundationWalletAddr { get; set; }
    public int Reward { get; set; }
    public int SwitchEpoch { get; set; }
    public long SwitchBlock { get; set; }
    public ulong RangeReturnSigner { get; set; }
    public Address[] GenesisMasternodes { get; set; } = Array.Empty<Address>();

    public Address BlockSignerContract { get; set; }
    public Address RandomizeSMCBinary { get; set; }
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; }
    public Address XDCXLendingAddressBinary { get; set; }
    public Address XDCXAddressBinary { get; set; }
    public Address TradingStateAddressBinary { get; set; }

    public Address MasternodeVotingContract { get; set; }

    public long LimitPenaltyEpoch { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public long LimitPenaltyEpochV2 { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public Address RelayerRegistrationSMC { get; set; }
    public Address TRC21IssuerSMC { get; set; }

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
    public long? TipTrc21Fee { get; set; }
    public long TIP2019Block { get; set; }
    public long? TipUpgradePenalty { get; set; }
    public long? TipUpgradeReward { get; set; }
    public UInt256 MasternodeReward { get; set; }
    public UInt256 ProtectorReward { get; set; }
    public UInt256 ObserverReward { get; set; }
    public long MergeSignRange { get; set; }
    public Address[] BlackListedAddresses { get; set; }
    public long BlackListHFNumber { get; set; }
    public long TipXDCX { get; set; }
    public long TIPXDCXMinerDisable { get; set; }
    public long? DynamicGasLimitBlock { get; set; }

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

    public void ApplyToReleaseSpec(ReleaseSpec spec, long startBlock, ulong? startTimestamp) => spec.BaseFeeCalculator = new XdcBaseFeeCalculator();

    public void AddTransitions(SortedSet<long> blockNumbers, SortedSet<ulong> timestamps)
    {
        if (TipTrc21Fee is not null)
            blockNumbers.Add(TipTrc21Fee.Value);
        if (TipUpgradePenalty is not null)
            blockNumbers.Add(TipUpgradePenalty.Value);
        if (TipUpgradeReward is not null)
            blockNumbers.Add(TipUpgradeReward.Value);
    }
}

public sealed class V2ConfigParams
{
    public ulong SwitchRound { get; init; }
    public int MaxMasternodes { get; init; }
    public double CertificateThreshold { get; init; }
    public int TimeoutSyncThreshold { get; init; }
    public int TimeoutPeriod { get; init; }
    public int MinePeriod { get; init; }
}
