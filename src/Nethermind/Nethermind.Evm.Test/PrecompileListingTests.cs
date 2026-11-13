// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class PrecompileListingTests
{
    private static IEnumerable<NamedReleaseSpec> Forks()
    {
        for (NamedReleaseSpec? fork = Bogota.Instance; fork is not null; fork = fork.Parent) yield return fork;
    }

    [TestCaseSource(nameof(Forks))]
    public void ListPrecompiles_lists_the_fork_precompile_set(NamedReleaseSpec fork) =>
        Assert.That(fork.ListPrecompiles().Values.Select(static a => new AddressAsKey(a)),
            Is.EquivalentTo(((IReleaseSpec)fork).Precompiles));
}
