// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class ReleaseSpecSubstitute
{
    public static IReleaseSpec Create() => Configure(Substitute.For<IReleaseSpec>());

    /// <summary>A frame-transaction fork with EIP-7906 as asked, and EIP-8141 with EIP-8288 only when dependency frames are wanted.</summary>
    public static IReleaseSpec CreateFrame(bool assertEnabled = true, bool dependencyEnabled = false)
    {
        IReleaseSpec spec = Create();
        spec.IsEip7906Enabled.Returns(assertEnabled);
        spec.IsEip8141Enabled.Returns(dependencyEnabled);
        spec.IsEip8288Enabled.Returns(dependencyEnabled);
        return spec;
    }

    /// <summary>Arranges the members a bare <see cref="IReleaseSpec"/> substitute cannot answer itself.</summary>
    /// <remarks>Chain-specific substitutes route through this rather than repeating the arrangement, so a
    /// member that has to be taught to a substitute is taught to all of them at once.</remarks>
    public static T Configure<T>(T sub) where T : class, IReleaseSpec
    {
        sub.GasCosts.Returns(_ => new SpecGasCosts(sub));
        // A substitute intercepts this default-implemented member too; answer with the pinned key a real spec defaults to.
        sub.Eip8288AggregatedVk.Returns(Eip8288Verifier.PinnedAggregatedVk);
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
