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
    public ulong Epoch { get; set; }
    public ulong Gap { get; set; }
    public ulong Period { get; set; }
    public bool SkipV1Validation { get; set; }
    public Address FoundationWalletAddr { get; set; } = null!;
    public ulong Reward { get; set; }
    public ulong SwitchEpoch { get; set; }
    public ulong SwitchBlock { get; set; }
    public ulong RangeReturnSigner { get; set; }
    public Address[] GenesisMasternodes { get; set; } = Array.Empty<Address>();

    public Address BlockSignerContract { get; set; } = null!;
    public Address RandomizeSMCBinary { get; set; } = null!;
    public Address XDCXLendingFinalizedTradeAddressBinary { get; set; } = null!;
    public Address XDCXLendingAddressBinary { get; set; } = null!;
    public Address XDCXAddressBinary { get; set; } = null!;
    public Address TradingStateAddressBinary { get; set; } = null!;

    public Address MasternodeVotingContract { get; set; } = null!;

    public ulong LimitPenaltyEpoch { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public ulong LimitPenaltyEpochV2 { get; set; }           // Epochs in a row that a penalty node needs to be penalized
    public Address RelayerRegistrationSMC { get; set; } = null!;
    public Address TRC21IssuerSMC { get; set; } = null!;

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
    public ulong? TipUpgradePenalty { get; set; }
    public ulong? TipUpgradeReward { get; set; }
    public UInt256 MasternodeReward { get; set; }
    public UInt256 ProtectorReward { get; set; }
    public UInt256 ObserverReward { get; set; }
    public ulong MergeSignRange { get; set; }
    public Address[] BlackListedAddresses { get; set; } = [];
    public ulong BlackListHFNumber { get; set; }
    public ulong TipXDCX { get; set; }
    public ulong TIPXDCXMinerDisable { get; set; }
    public ulong? DynamicGasLimitBlock { get; set; }

    private readonly struct V2ConfigBySwitchRoundComparer : IComparer<V2ConfigParams>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(V2ConfigParams? a, V2ConfigParams? b) => a?.SwitchRound.CompareTo(b?.SwitchRound ?? 0) ?? (b is null ? 0 : -1);
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
    }
}

public sealed class V2ConfigParams
{
    public ulong SwitchRound { get; init; }
    public int MaxMasternodes { get; init; }
    public double CertificateThreshold { get; init; }
    public int TimeoutSyncThreshold { get; init; }
    public int TimeoutPeriod { get; init; }
    public ulong MinePeriod { get; init; }
}
