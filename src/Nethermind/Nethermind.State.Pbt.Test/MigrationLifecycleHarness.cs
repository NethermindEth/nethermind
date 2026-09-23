// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

internal sealed class MigrationLifecycleHarness(IContainer container, Dictionary<string, Block> blocks, Dictionary<string, JsonElement> expected, string fixtureDirectory) : IAsyncDisposable
{
    internal static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");
    public string FixtureDirectory => fixtureDirectory;
    public IContainer Container => container;
    public IBlockTree Tree => container.Resolve<IBlockTree>();
    public IPbtDbManager Pbt => container.Resolve<IPbtDbManager>();
    public IStateReader Reader => container.Resolve<IWorldStateManager>().GlobalStateReader;
    public IMigrationTelemetry Telemetry => container.Resolve<IMigrationTelemetry>();
    public PbtBalFollowerScheduler Scheduler => container.Resolve<PbtBalFollowerScheduler>();
    public Block Anchor => blocks["anchor"];
    public Dictionary<string, Block> Blocks => blocks;
    public Dictionary<string, JsonElement> Expected => expected;
    public ValueTask DisposeAsync() => container.DisposeAsync();

    public static async Task<MigrationLifecycleHarness> Create(string targetPath, bool portable, Action<ContainerBuilder>? configure = null, string? fixtureDirectory = null, Action<PbtConfig>? configureMigration = null, bool migration = true)
    {
        string fixtures = fixtureDirectory ?? Fixtures;
        using Stream genesisInput = fixtureDirectory is null
            ? typeof(MigrationLifecycleHarness).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!
            : File.OpenRead(Path.Combine(fixtures, "genesis.json"));
        ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesisInput);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "blocks.json")));
        Dictionary<string, Block> blocks = [];
        Dictionary<string, JsonElement> expected = [];
        foreach (JsonElement item in fixture.RootElement.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            Block block = Rlp.Decode<Block>(new Rlp(Bytes.FromHexString(item.GetProperty("blockRlp").GetString()!)))!;
            blocks.Add(name, block);
            expected.Add(name, item.Clone());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        PbtConfig config = new() { Enabled = true, MigrationGenesisBootstrap = !portable };
        if (portable)
        {
            config.MigrationSnapshotPath = Path.Combine(fixtures, "canonical", "anchor", "snapshot.pbt");
            config.MigrationPreimagesPath = Path.Combine(fixtures, "canonical", "anchor", "preimages.bin");
            config.MigrationAnchor = (long)blocks["anchor"].Number;
        }
        configureMigration?.Invoke(config);
        FlatDbConfig flatConfig = new() { Enabled = true, Layout = FlatLayout.Flat };
        InitConfig initConfig = new() { BaseDbPath = targetPath };
        BlocksConfig blocksConfig = new() { PreWarming = PreWarmMode.None };
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(flatConfig, initConfig, blocksConfig), chain, useTestSpecProvider: false))
            .AddSingleton<IPbtConfig>(config);
        if (migration) builder.AddModule(new PbtPlugin(config, flatConfig, chain, initConfig).Module!);
        configure?.Invoke(builder);
        IContainer container = builder.Build();
        try
        {
            IWorldStateManager manager = container.Resolve<IWorldStateManager>();
            IBlockTree tree = container.Resolve<IBlockTree>();
            if (tree.Genesis is null)
            {
                using (ILifetimeScope genesisScope = container.BeginLifetimeScope(builder => builder.AddSingleton<IWorldStateScopeProvider>(manager.GlobalWorldState)))
                {
                    IWorldState state = genesisScope.Resolve<IWorldState>();
                    using (state.BeginScope(null))
                    {
                        Block genesis = genesisScope.Resolve<IGenesisBuilder>().Build();
                        Assert.That(genesis.Hash, Is.EqualTo(blocks["anchor"].Hash), "independent geth genesis identity");
                    }
                }
                tree.SuggestBlock(blocks["anchor"]);
                tree.TryUpdateMainChain(blocks["anchor"].Header, true, true, [blocks["anchor"]]);
                manager.FlushCache(CancellationToken.None);
            }
            if (migration) await container.Resolve<InitializePbtMigration>().Execute(CancellationToken.None);
            return new(container, blocks, expected, fixtures);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }
}
