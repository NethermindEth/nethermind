// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;

namespace Nethermind.Optimism;

public class OptimismNethermindApi(NethermindApi.Dependencies dependencies, IOptimismSpecHelper helper, ICostHelper costHelper) : NethermindApi(dependencies)
{
    public ICostHelper L1CostHelper { get; } = costHelper;
    public IOptimismSpecHelper SpecHelper { get; } = helper;
}
