// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche;

public class AvalancheChainSpecBasedSpecProvider(
    ChainSpec chainSpec,
    AvalancheChainSpecEngineParameters engineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    protected override ReleaseSpec CreateEmptyReleaseSpec() => new AvalancheReleaseSpec();

    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, ulong releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        AvalancheReleaseSpec releaseSpec = (AvalancheReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.IsApricotPhase3Enabled = (engineParameters.ApricotPhase3Timestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsDurangoEnabled = (engineParameters.DurangoTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsEtnaEnabled = (engineParameters.EtnaTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsFortunaEnabled = (engineParameters.FortunaTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsGraniteEnabled = (engineParameters.GraniteTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

        return releaseSpec;
    }
}
