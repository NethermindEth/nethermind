// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class ReleaseSpecSubstitute
{
    public static IReleaseSpec Create()
    {
        IReleaseSpec sub = Substitute.For<IReleaseSpec>();
        sub.GasCosts.Returns(_ => new SpecGasCosts(sub));
        // A substitute intercepts IsPrecompile rather than running the interface's default body, so route
        // it back through Precompiles: a test that arranges the set still gets the membership it arranged.
        sub.IsPrecompile(Arg.Any<Address>()).Returns(call =>
        {
            Address address = (Address)call[0];
            return address.CouldBePrecompile() && sub.Precompiles.Contains(address);
        });
        return sub;
    }
}
