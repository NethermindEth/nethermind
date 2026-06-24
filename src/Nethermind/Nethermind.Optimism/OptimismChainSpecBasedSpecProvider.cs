// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismChainSpecBasedSpecProvider(
    ChainSpec chainSpec,
    OptimismChainSpecEngineParameters engineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    protected override ReleaseSpec CreateEmptyReleaseSpec() => new OptimismReleaseSpec();

    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        OptimismReleaseSpec releaseSpec = (OptimismReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.IsOpGraniteEnabled = (engineParameters.GraniteTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsOpHoloceneEnabled = (engineParameters.HoloceneTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsOpIsthmusEnabled = (engineParameters.IsthmusTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsOpJovianEnabled = (engineParameters.JovianTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsOpKarstEnabled = (engineParameters.KarstTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

        return releaseSpec;
    }
}
