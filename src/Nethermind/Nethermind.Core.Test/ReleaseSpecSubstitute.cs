// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class ReleaseSpecSubstitute
{
    public static IReleaseSpec Create() => Configure(Substitute.For<IReleaseSpec>());

    /// <summary>Arranges the members a bare <see cref="IReleaseSpec"/> substitute cannot answer itself.</summary>
    /// <remarks>Chain-specific substitutes route through this rather than repeating the arrangement, so a
    /// member that has to be taught to a substitute is taught to all of them at once.</remarks>
    public static T Configure<T>(T sub) where T : class, IReleaseSpec
    {
        sub.GasCosts.Returns(_ => new SpecGasCosts(sub));
        // A substitute intercepts IsPrecompile rather than running the interface's default body, so route
        // it back through Precompiles: a test that arranges the set still gets the membership it arranged.
        // The read goes through the substitute, so a Received() assertion on Precompiles counts it too.
        sub.IsPrecompile(Arg.Any<Address>()).Returns(call =>
        {
            Address address = (Address)call[0];
            return address.CouldBePrecompile() && sub.Precompiles.Contains(address);
        });
        return sub;
    }
}
