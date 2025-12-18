// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;

namespace Nethermind.Optimism;

public class OptimismNethermindApi : NethermindApi
{
    public OptimismNethermindApi(Dependencies dependencies, IOptimismSpecHelper helper, ICostHelper costHelper) : base(dependencies)
    {
        SpecHelper = helper;
        L1CostHelper = costHelper;
    }

    public ICostHelper L1CostHelper { get; }
    public IOptimismSpecHelper SpecHelper { get; }
}
