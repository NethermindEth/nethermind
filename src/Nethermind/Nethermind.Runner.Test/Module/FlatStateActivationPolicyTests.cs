// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatStateActivationPolicyTests
{
    // Branch 1: Enabled=false → false, regardless of db content
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(false, false, false, false, false, Description = "Disabled → always false")]
    [TestCase(true, true, false, false, true, Description = "Flat has committed state → true")]
    [TestCase(true, false, true, false, true, Description = "ImportFromPruningTrieState=true → true")]
    [TestCase(true, false, false, true, false, Description = "Patricia has data → false")]
    [TestCase(true, false, false, false, true, Description = "Fresh node, flat enabled → true")]
    public void ShouldTurnOnFlatDb_ReturnsExpected(
        bool enabled, bool flatHasData, bool importFromPruningTrieState, bool patriciaHasData, bool expected)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(enabled);
        flatDbConfig.ImportFromPruningTrieState.Returns(importFromPruningTrieState);

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flatHasData ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero) : StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);

        MemDb patriciaDb = new();
        if (patriciaHasData)
            patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(flatDbConfig, new Lazy<IPersistence>(() => flatPersistence), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance);
        policy.ShouldTurnOnFlatDb().Should().Be(expected);
    }
}
