// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismNethermindApi : NethermindApi
{
    public OptimismNethermindApi(ILifetimeScope lifetimeScope) : base(lifetimeScope)
    {
    }

    public IInvalidChainTracker? InvalidChainTracker { get; set; }
    public OPL1CostHelper? L1CostHelper { get; set; }
    public OptimismSpecHelper? SpecHelper { get; set; }
}
