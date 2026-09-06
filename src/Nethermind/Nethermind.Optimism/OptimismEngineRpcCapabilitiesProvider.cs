// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.HealthChecks;

namespace Nethermind.Optimism;

public class OptimismEngineRpcCapabilitiesProvider(ISpecProvider specProvider) : EngineRpcCapabilitiesProvider(specProvider)
{
    protected override bool IsV4Enabled(IReleaseSpec spec) =>
        base.IsV4Enabled(spec) || (spec is IOptimismReleaseSpec op && op.IsOpIsthmusEnabled);
}
