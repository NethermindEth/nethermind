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
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

internal sealed class MigrationLifecycleHarness(IContainer container, Dictionary<string, Block> blocks, Dictionary<string, JsonElement> expected, string fixtureDirectory) : IAsyncDisposable
{
    public string FixtureDirectory => fixtureDirectory;
    public IContainer Container => container;
    public IBlockTree Tree => container.Resolve<IBlockTree>();
    public IPbtDbManager Pbt => container.Resolve<IPbtDbManager>();
    public IStateReader Reader => container.Resolve<IWorldStateManager>().GlobalStateReader;
    public IMigrationTelemetry Telemetry => container.Resolve<IMigrationTelemetry>();
    public PbtBalFollowerScheduler Scheduler => container.Resolve<PbtBalFollowerScheduler>();
    public Block Anchor => blocks["anchor"];

    /// <summary>Waits until the background BAL replay has brought PBT up to <paramref name="header"/>.</summary>
    public void WaitForPbt(BlockHeader header) =>
        Assert.That(() => Pbt.HasStateForBlock(new StateId(header)), Is.True.After(30_000, 50), header.ToString(BlockHeader.Format.Short));
    public Dictionary<string, Block> Blocks => blocks;
    public Dictionary<string, JsonElement> Expected => expected;
    public ValueTask DisposeAsync() => container.DisposeAsync();

    public Hash256 PbtRoot(string name) => new(expected[name].GetProperty("pbtRoot").GetString()!);

    /// <summary>The geth-recorded root of the tree the header does not commit to: PBT before activation, MPT after.</summary>
    public Hash256 ExpectedShadowRoot(string name) =>
        new(expected[name].GetProperty(expected[name].GetProperty("binary").GetBoolean() ? "mptRoot" : "pbtRoot").GetString()!);

    public static UInt256 ParseQuantity(string value) => new(Bytes.FromHexString(value.Length % 2 == 0 ? value : "0x0" + value[2..]), true);

    /// <summary>Asserts every account of the fixture's reference allocation for <paramref name="name"/> through <see cref="Reader"/>.</summary>
    public void AssertAllocation(string name)
    {
        BlockHeader header = blocks[name].Header;
        using JsonDocument allocation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "states", name + ".alloc.json")));
        IStateReader reader = Reader;
        foreach (JsonProperty account in allocation.RootElement.EnumerateObject())
        {
            Address address = new(account.Name);
            JsonElement value = account.Value;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetBalance(header, address), Is.EqualTo(ParseQuantity(value.GetProperty("balance").GetString()!)), $"{name} balance {address}");
                Assert.That(reader.GetNonce(header, address), Is.EqualTo(value.TryGetProperty("nonce", out JsonElement nonce) ? (ulong)ParseQuantity(nonce.GetString()!) : 0UL), $"{name} nonce {address}");
                Assert.That(reader.GetCode(header, address), Is.EqualTo(value.TryGetProperty("code", out JsonElement code) ? Bytes.FromHexString(code.GetString()!) : []), $"{name} code {address}");
            }
            if (value.TryGetProperty("storage", out JsonElement storage))
                foreach (JsonProperty slot in storage.EnumerateObject())
                    Assert.That(reader.GetStorage(header, address, ParseQuantity(slot.Name)), Is.EqualTo(ParseQuantity(slot.Value.GetString()!)), $"{name} slot {address}/{slot.Name}");
        }
    }

    public static async Task<MigrationLifecycleHarness> Create(string targetPath, bool portable, FlatLayout layout, string fixtureDirectory, Action<ContainerBuilder>? configure = null, bool migration = true)
    {
        using Stream genesisInput = File.OpenRead(Path.Combine(fixtureDirectory, "genesis.json"));
        ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesisInput);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "blocks.json")));
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
            config.MigrationSnapshotPath = Path.Combine(fixtureDirectory, "canonical", "anchor", "snapshot.pbt");
            config.MigrationPreimagesPath = Path.Combine(fixtureDirectory, "canonical", "anchor", "preimages.bin");
            config.MigrationAnchor = (long)blocks["anchor"].Number;
        }
        FlatDbConfig flatConfig = new() { Enabled = true, Layout = layout };
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
            if (migration)
            {
                await container.Resolve<InitializePbtMigration>().Execute(CancellationToken.None);
                PbtMigrationImport import = container.Resolve<PbtMigrationImport>();
                await import.Completion;
                Assert.That(import.Error, Is.Null);
            }
            return new(container, blocks, expected, fixtureDirectory);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }
}
