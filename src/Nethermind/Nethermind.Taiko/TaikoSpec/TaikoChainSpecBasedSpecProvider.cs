// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoChainSpecBasedSpecProvider(ChainSpec chainSpec,
    TaikoChainSpecEngineParameters chainSpecEngineParameters,
    ILogManager logManager)
    : ChainSpecBasedSpecProvider(chainSpec, logManager)
{
    protected override ReleaseSpec CreateEmptyReleaseSpec() => new TaikoReleaseSpec
    {
        TaikoL2Address = Address.Zero
    };

    protected override ReleaseSpec CreateReleaseSpec(ChainSpec chainSpec, long releaseStartBlock, ulong? releaseStartTimestamp = null)
    {
        TaikoReleaseSpec releaseSpec = (TaikoReleaseSpec)base.CreateReleaseSpec(chainSpec, releaseStartBlock, releaseStartTimestamp);

        releaseSpec.IsOntakeEnabled = (chainSpecEngineParameters.OntakeTransition ?? long.MaxValue) <= releaseStartBlock;
        releaseSpec.IsPacayaEnabled = (chainSpecEngineParameters.PacayaTransition ?? long.MaxValue) <= releaseStartBlock;
        releaseSpec.IsShastaEnabled = (chainSpecEngineParameters.ShastaTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.UseSurgeGasPriceOracle = chainSpecEngineParameters.UseSurgeGasPriceOracle ?? false;
        releaseSpec.TaikoL2Address = chainSpecEngineParameters.TaikoL2Address;
        releaseSpec.IsRip7728Enabled = (chainSpecEngineParameters.Rip7728TransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;
        releaseSpec.IsL1StaticCallEnabled = (chainSpecEngineParameters.L1StaticCallTransitionTimestamp ?? ulong.MaxValue) <= releaseStartTimestamp;

        return releaseSpec;
    }
}
