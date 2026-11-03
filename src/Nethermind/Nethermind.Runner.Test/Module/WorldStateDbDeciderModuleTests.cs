// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture(FlatLayout.Flat)]
[TestFixture(FlatLayout.PaprikaFlat)]
[Parallelizable(ParallelScope.All)]
public class WorldStateDbDeciderModuleTests(FlatLayout layout)
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
    [TestCase(Flags.Enabled | Flags.FlatHasData | Flags.PatriciaHasData, true, Description = "Committed flat state wins over patricia data → flat")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, true, Description = "ImportFromPruningTrieState → flat")]
    [TestCase(Flags.Enabled | Flags.PatriciaHasData, false, Description = "Patricia has data → patricia")]
    [TestCase(Flags.Enabled, true, Description = "Fresh node, flat enabled → flat")]
    public void IWorldStateManager_ResolvesToCorrectBackend(Flags flags, bool expectFlat)
    {
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(CreateModule(baseDbPath))
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = flags.HasFlag(Flags.Enabled);
                cfg.ImportFromPruningTrieState = flags.HasFlag(Flags.ImportFromPruningTrieState);
            })
            .Build();

        // Populate DBs before FlatStateActivationPolicy is evaluated.
        // The policy is a lazy singleton — it's only constructed when IWorldStateManager is first resolved.
        if (flags.HasFlag(Flags.FlatHasData))
            WriteFlatCurrentState(container, 1);

        if (flags.HasFlag(Flags.PatriciaHasData))
            container.ResolveKeyed<IDb>(DbNames.State).Set(Bytes.FromHexString("0000000000000001"), [1]);

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        if (expectFlat)
            Assert.That(worldStateManager, Is.TypeOf<FlatWorldStateManager>());
        else
            Assert.That(worldStateManager, Is.Not.TypeOf<FlatWorldStateManager>());
    }

    [TestCase(true, FullPruningTrigger.StateDbSize, 1, Description = "Flat + automatic trigger → warns once")]
    [TestCase(true, FullPruningTrigger.Manual, 0, Description = "Flat + Manual → silent")]
    [TestCase(false, FullPruningTrigger.StateDbSize, 0, Description = "Patricia + automatic trigger → silent")]
    public void Ignored_full_prune_warning_fires_only_on_flat_with_automatic_trigger(bool flatEnabled, FullPruningTrigger trigger, int expectedWarnings)
    {
        TestLogger testLogger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(CreateModule(baseDbPath))
            .AddSingleton<ILogManager>(new OneLoggerLogManager(new(testLogger)))
            .Intercept<IFlatDbConfig>((cfg) => cfg.Enabled = flatEnabled)
            .Intercept<IPruningConfig>((cfg) => { cfg.FullPruningTrigger = trigger; cfg.Mode = PruningMode.Memory; })
            .Build();

        container.Resolve<IWorldStateManager>();

        Assert.That(testLogger.LogList.Count(l => l.Contains("ignored on the Flat backend")), Is.EqualTo(expectedWarnings));
    }

    [Flags]
    public enum PointerSeed
    {
        None = 0,
        BlockInfosEntry = 1,
    }

    [TestCase(Flags.None, PointerSeed.None, null, Description = "Patricia, nothing stored → null")]
    [TestCase(Flags.None, PointerSeed.BlockInfosEntry, 936ul, Description = "Patricia reads the BlockInfos pointer")]
    [TestCase(Flags.Enabled | Flags.FlatHasData, PointerSeed.None, 1ul, Description = "Flat reads its persisted CurrentState")]
    [TestCase(Flags.Enabled, PointerSeed.BlockInfosEntry, null, Description = "Flat ignores the BlockInfos entry (PreGenesis → null)")]
    [TestCase(Flags.Enabled | Flags.ImportFromPruningTrieState, PointerSeed.BlockInfosEntry, 936ul, Description = "Import mode falls back to the trie pointer while flat is empty")]
    public void IStateBoundary_ReadsBackendPointer(Flags flags, PointerSeed seed, ulong? expected)
    {
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(CreateModule(baseDbPath))
            .Intercept<IFlatDbConfig>((cfg) =>
            {
                cfg.Enabled = flags.HasFlag(Flags.Enabled);
                cfg.ImportFromPruningTrieState = flags.HasFlag(Flags.ImportFromPruningTrieState);
            })
            .Build();

        if (flags.HasFlag(Flags.FlatHasData))
            WriteFlatCurrentState(container, 1);
        if (seed.HasFlag(PointerSeed.BlockInfosEntry))
            // The block tree's long-standing best-persisted-state key: 16 zero bytes.
            container.ResolveKeyed<IDb>(DbNames.BlockInfos).Set(new byte[16], Rlp.Encode(936UL).Bytes);

        Assert.That(container.Resolve<IStateBoundary>().BestPersistedState, Is.EqualTo(expected));
        // IStateBoundary is injected into BlockTree's constructor; resolving the tree proves the
        // graph stays cycle-free (the full IWorldStateManager graph would resolve the tree back).
        Assert.DoesNotThrow(() => container.Resolve<IBlockTree>());
    }

    /// <summary>
    /// A node on the fixture's layout whose files go under <paramref name="baseDbPath"/>: the trie node log PaprikaFlat
    /// requires, while its Paprika store stays in memory.
    /// </summary>
    private TestNethermindModule CreateModule(TempPath baseDbPath) => new(
        new FlatDbConfig { Layout = layout, TrieNodeLogEnabled = layout == FlatLayout.PaprikaFlat, PaprikaFlatFileSizeBytes = 64 * 1024 * 1024 },
        new InitConfig { BaseDbPath = baseDbPath.Path, DiagnosticMode = DiagnosticMode.MemDb });

    // Format: 8-byte big-endian block number + 32-byte state root (matches BasePersistence encoding).
    // PaprikaFlat keeps the state pointer in Paprika, which only a persistence commit writes.
    private void WriteFlatCurrentState(IContainer container, ulong blockNumber)
    {
        if (layout == FlatLayout.PaprikaFlat)
        {
            container.Resolve<IPersistence>().CreateWriteBatch(StateId.PreGenesis, new StateId(blockNumber, ValueKeccak.Zero), WriteFlags.None).Dispose();
            return;
        }

        IDb metadataDb = container.Resolve<IColumnsDb<FlatDbColumns>>().GetColumnDb(FlatDbColumns.Metadata);
        byte[] value = new byte[8 + 32];
        BinaryPrimitives.WriteUInt64BigEndian(value, blockNumber);
        metadataDb.Set(Keccak.Compute("CurrentState").BytesToArray(), value);
    }
}
