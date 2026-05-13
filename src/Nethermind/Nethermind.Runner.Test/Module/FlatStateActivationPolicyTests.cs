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
    [Flags]
    public enum Flags
    {
        None = 0,
        Enabled = 1,
        FlatHasData = 2,
        ImportFromPruningTrieState = 4,
        PatriciaHasData = 8
    }

    // Branch 1: Enabled=false → false, regardless of db content
    // Branch 2: Enabled=true, flat persistence has committed state → true
    // Branch 3: Enabled=true, no committed state, ImportFromPruningTrieState=true → true
    // Branch 4: Enabled=true, no committed state, ImportFromPruningTrieState=false, patricia has data → false
    // Branch 5: Enabled=true, no committed state, ImportFromPruningTrieState=false, no patricia data → true
    [TestCase(Flags.None, false, Description = "Disabled → always false")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, true, Description = "Flat has committed state → true")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, true, Description = "ImportFromPruningTrieState=true → true")]
    [TestCase(Flags.Enabled | Flags.PatriciaHasData, false, Description = "Patricia has data → false")]
    [TestCase(Flags.Enabled, true, Description = "Fresh node, flat enabled → true")]
    public void ShouldTurnOnFlatDb_ReturnsExpected(Flags flags, bool expected)
    {
        IFlatDbConfig flatDbConfig = Substitute.For<IFlatDbConfig>();
        flatDbConfig.Enabled.Returns(flags.HasFlag(Flags.Enabled));
        flatDbConfig.ImportFromPruningTrieState.Returns(flags.HasFlag(Flags.ImportFromPruningTrieState));

        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(flags.HasFlag(Flags.FlatHasData) ? new StateId(1, Nethermind.Core.Crypto.Keccak.Zero) : StateId.PreGenesis);
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        flatPersistence.CreateReader().Returns(reader);

        MemDb patriciaDb = new();
        if (flags.HasFlag(Flags.PatriciaHasData))
            patriciaDb.Set([1], [1]);

        FlatStateActivationPolicy policy = new(flatDbConfig, new Lazy<IPersistence>(() => flatPersistence), new Lazy<IDb>(() => patriciaDb), LimboLogs.Instance);
        policy.ShouldTurnOnFlatDb().Should().Be(expected);
    }
}
