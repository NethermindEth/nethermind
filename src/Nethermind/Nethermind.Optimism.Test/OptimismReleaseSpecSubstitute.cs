// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Optimism.Test;

public static class OptimismReleaseSpecSubstitute
{
    public static IOptimismReleaseSpec Create()
    {
        IOptimismReleaseSpec sub = Substitute.For<IOptimismReleaseSpec>();
        sub.GasCosts.Returns(_ => new SpecGasCosts(sub));
        return sub;
    }
}
