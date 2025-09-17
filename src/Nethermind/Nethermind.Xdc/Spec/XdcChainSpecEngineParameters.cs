// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Spec;
public class XdcChainSpecEngineParameters : IChainSpecEngineParameters
{
    public string EngineName => SealEngineType;
    public string SealEngineType => Core.SealEngineType.XDPoS;
    public int Epoch { get; set; }
    public int Gap { get; set; }
    public int Period { get; set; }
    public bool SkipV1Validation { get; set; }
    public Address FoundationWalletAddr { get; set; }
    public int Reward { get; set; }
    public int SwitchEpoch { get; set; }
    public UInt256 SwitchBlock { get; set; }


    private List<V2ConfigParams> _v2Configs = new();
    public List<V2ConfigParams> V2Configs
    {
        get => _v2Configs;
        set
        {
            _v2Configs = value ?? new();
            _v2Configs.Sort((a, b) => a.SwitchRound.CompareTo(b.SwitchRound));
            CheckConfig(_v2Configs);
        }
    }

    private static void CheckConfig(List<V2ConfigParams> list)
    {
        if (list.Count == 0 || list[0].SwitchRound != 0)
            throw new InvalidOperationException("There should be a default configuration with switchRound = 0");
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].SwitchRound == list[i - 1].SwitchRound)
                throw new InvalidOperationException($"Duplicate config for round {list[i].SwitchRound}.");
        }
    }
}

public sealed class V2ConfigParams
{
    public ulong SwitchRound { get; init; }
    public int MaxMasternodes { get; init; }
    public double CertThreshold { get; init; }
    public int TimeoutSyncThreshold { get; init; }
    public int TimeoutPeriod { get; init; }
    public int MinePeriod { get; init; }
}

