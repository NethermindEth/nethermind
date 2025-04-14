// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;

namespace Nethermind.Optimism;

public class OptimismNethermindApi : NethermindApi
{
    public OptimismNethermindApi(Dependencies dependencies, IOptimismSpecHelper helper) : base(dependencies)
    {
        SpecHelper = helper;
    }

    public OPL1CostHelper? L1CostHelper { get; set; }
    public IOptimismSpecHelper SpecHelper { get; private set; }
}
