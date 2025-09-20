// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Xdc.Spec;

public class XdcChainSpecBasedSpecProvider(ChainSpec chainSpec,
    XdcChainSpecEngineParameters chainSpecEngineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    protected override ReleaseSpec CreateEmptyReleaseSpec() => new XdcV2ReleaseSpec();
    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        var releaseSpec = (XdcV2ReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.SwitchEpoch = chainSpecEngineParameters.SwitchEpoch;
        releaseSpec.SwitchBlock = chainSpecEngineParameters.SwitchBlock;

        ApplyV2Config(releaseSpec, 0);

        return releaseSpec;
    }

    public void ApplyV2Config(XdcV2ReleaseSpec releaseSpec, ulong round)
    {
        V2ConfigParams configParams = GetConfigAtRound(chainSpecEngineParameters.V2Configs, round);
        releaseSpec.SwitchRound = configParams.SwitchRound;
        releaseSpec.MaxMasternodes = configParams.MaxMasternodes;
        releaseSpec.CertThreshold = configParams.CertThreshold;
        releaseSpec.TimeoutSyncThreshold = configParams.TimeoutSyncThreshold;
        releaseSpec.TimeoutPeriod = configParams.TimeoutPeriod;
        releaseSpec.MinePeriod = configParams.MinePeriod;
    }

    internal static V2ConfigParams GetConfigAtRound(List<V2ConfigParams> list, ulong round)
    {
        // list.Count >= 1 and list[0].SwitchRound == 0 guaranteed by CheckConfig
        int lo = 0, hi = list.Count; // [lo,hi)
        while (lo + 1 < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (list[mid].SwitchRound <= round) lo = mid;
            else hi = mid;
        }
        return list[lo];
    }

}
