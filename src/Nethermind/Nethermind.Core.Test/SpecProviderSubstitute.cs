// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class SpecProviderSubstitute
{
    public static ISpecProvider Create()
    {
        ISpecProvider sub = Substitute.For<ISpecProvider>();
        sub.GenesisSpec.Returns(ReleaseSpecSubstitute.Create());
        sub.GetSpec(Arg.Any<ForkActivation>()).Returns(call => ReleaseSpecSubstitute.Create());
        return sub;
    }
}
