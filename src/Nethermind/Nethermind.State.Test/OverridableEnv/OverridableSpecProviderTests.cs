// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Store.Test.OverridableEnv;

public class OverridableSpecProviderTests
{
    [Test]
    public void Reads_genesis_state_unavailable_from_the_inner_provider([Values] bool unavailable)
    {
        ChainSpec chainSpec = new() { GenesisStateUnavailable = unavailable };
        ISpecProvider provider = new OverridableSpecProvider(new ChainSpecBasedSpecProvider(chainSpec));

        bool before = provider.GenesisStateUnavailable;
        chainSpec.GenesisStateUnavailable = !unavailable;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(unavailable));
            Assert.That(provider.GenesisStateUnavailable, Is.EqualTo(!unavailable), "read on each call, not captured");
        }
    }
}
