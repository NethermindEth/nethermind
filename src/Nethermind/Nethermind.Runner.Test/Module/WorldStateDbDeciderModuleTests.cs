// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    // Mirrors the 5 branches in FlatStateActivationPolicy at the full DI container level.
    [TestCase(false, false, false, false, false, Description = "Flat disabled → patricia")]
    [TestCase(true, true, false, false, true, Description = "Flat has committed state → flat")]
    [TestCase(true, false, true, false, true, Description = "ImportFromPruningTrieState → flat")]
    [TestCase(true, false, false, true, false, Description = "Patricia has data → patricia")]
    [TestCase(true, false, false, false, true, Description = "Fresh node, flat enabled → flat")]
    public void IWorldStateManager_ResolvesToCorrectBackend(
        bool enabled, bool flatHasData, bool importFromPruningTrieState, bool patriciaHasData, bool expectFlat)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = enabled;
                cfg.ImportFromPruningTrieState = importFromPruningTrieState;
            })
            .Build();

        // Populate DBs before FlatStateActivationPolicy is evaluated.
        // The policy is a lazy singleton — it's only constructed when IWorldStateManager is first resolved.
        if (flatHasData)
        {
            // Write a non-PreGenesis current state into the flat DB metadata column.
            // Format: 8-byte big-endian block number + 32-byte state root (matches BasePersistence encoding).
            IDb metadataDb = container.Resolve<IColumnsDb<FlatDbColumns>>().GetColumnDb(FlatDbColumns.Metadata);
            byte[] key = Keccak.Compute("CurrentState").BytesToArray();
            byte[] value = new byte[8 + 32];
            BinaryPrimitives.WriteInt64BigEndian(value, 1);
            metadataDb.Set(key, value);
        }

        if (patriciaHasData)
            container.ResolveKeyed<IDb>(DbNames.State).Set(Bytes.FromHexString("0000000000000001"), [1]);

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        if (expectFlat)
            worldStateManager.Should().BeOfType<FlatWorldStateManager>();
        else
            worldStateManager.Should().NotBeOfType<FlatWorldStateManager>();
    }
}
