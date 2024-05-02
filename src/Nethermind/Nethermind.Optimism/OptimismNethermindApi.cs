// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Optimism;

public class OptimismNethermindApi : NethermindApi
{
    public OptimismNethermindApi(ILifetimeScope container) : base(container)
    {
    }

    public IInvalidChainTracker? InvalidChainTracker { get; set; }
    public OPL1CostHelper? L1CostHelper { get; set; }
    public OPSpecHelper? SpecHelper { get; set; }
}
