// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class WorldStateDbDeciderModuleTests
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

    // Mirrors the 5 branches in FlatStateActivationPolicy at the full DI container level.
    [TestCase(Flags.None, false, Description = "Flat disabled → patricia")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, true, Description = "Flat has committed state → flat")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, true, Description = "ImportFromPruningTrieState → flat")]
    [TestCase(Flags.Enabled | Flags.PatriciaHasData, false, Description = "Patricia has data → patricia")]
    [TestCase(Flags.Enabled, true, Description = "Fresh node, flat enabled → flat")]
    public void IWorldStateManager_ResolvesToCorrectBackend(Flags flags, bool expectFlat)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = flags.HasFlag(Flags.Enabled);
                cfg.ImportFromPruningTrieState = flags.HasFlag(Flags.ImportFromPruningTrieState);
            })
            .Build();

        // Populate DBs before FlatStateActivationPolicy is evaluated.
        // The policy is a lazy singleton — it's only constructed when IWorldStateManager is first resolved.
        if (flags.HasFlag(Flags.FlatHasData))
        {
            // Write a non-PreGenesis current state into the flat DB metadata column.
            // Format: 8-byte big-endian block number + 32-byte state root (matches BasePersistence encoding).
            IDb metadataDb = container.Resolve<IColumnsDb<FlatDbColumns>>().GetColumnDb(FlatDbColumns.Metadata);
            byte[] key = Keccak.Compute("CurrentState").BytesToArray();
            byte[] value = new byte[8 + 32];
            BinaryPrimitives.WriteInt64BigEndian(value, 1);
            metadataDb.Set(key, value);
        }

        if (flags.HasFlag(Flags.PatriciaHasData))
            container.ResolveKeyed<IDb>(DbNames.State).Set(Bytes.FromHexString("0000000000000001"), [1]);

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        if (expectFlat)
            worldStateManager.Should().BeOfType<FlatWorldStateManager>();
        else
            worldStateManager.Should().NotBeOfType<FlatWorldStateManager>();
    }
}
