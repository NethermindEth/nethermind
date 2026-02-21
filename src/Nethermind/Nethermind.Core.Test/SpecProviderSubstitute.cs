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
        IReleaseSpec genesis = ReleaseSpecSubstitute.Create();
        sub.GenesisSpec.Returns(genesis);
        IReleaseSpec spec = sub.GetSpec(Arg.Any<ForkActivation>());
        spec.GasCosts.Returns(new SpecGasCosts(spec));
        return sub;
    }
}
