// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test;
using NSubstitute;

namespace Nethermind.Optimism.Test;

public static class OptimismReleaseSpecSubstitute
{
    public static IOptimismReleaseSpec Create() =>
        ReleaseSpecSubstitute.Configure(Substitute.For<IOptimismReleaseSpec>());
}
