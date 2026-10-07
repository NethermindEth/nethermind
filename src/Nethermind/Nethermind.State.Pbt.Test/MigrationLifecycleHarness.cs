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
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.State.Flat;
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

    public byte[] BalRlp(string name) => Bytes.FromHexString(expected[name].GetProperty("balRlp").GetString()!);

    /// <summary>Stores the fixture BAL of <paramref name="name"/>, recovers its senders and suggests the block to <see cref="Tree"/>.</summary>
    public void Prepare(string name)
    {
        Block block = blocks[name];
        block.Header.IsPostMerge = true;
        block.EncodedBlockAccessList = BalRlp(name);
        block.BlockAccessList = Rlp.Decode<ReadOnlyBlockAccessList>(block.EncodedBlockAccessList);
        container.Resolve<IBlockAccessListStore>().Insert(block.Number, block.Hash!, block.EncodedBlockAccessList);
        foreach (IBlockPreprocessorStep preprocessor in container.Resolve<IReadOnlyList<IBlockPreprocessorStep>>()) preprocessor.RecoverData(block);
        Tree.SuggestBlock(block);
    }

    /// <summary>Prepares and processes <paramref name="names"/> in order, waiting for PBT at the parent of each binary block.</summary>
    /// <param name="names">The fixture blocks of one branch, parents first.</param>
    /// <param name="expectPbt">Whether to also assert the shadow root PBT reports for every processed block.</param>
    public void ProcessBranch(string[] names, bool expectPbt)
    {
        foreach (string name in names) Prepare(name);
        IBlockchainProcessor processor = container.Resolve<IMainProcessingContext>().BlockchainProcessor;
        foreach (string name in names)
        {
            Block block = blocks[name];
            if (expected[name].GetProperty("binary").GetBoolean())
                WaitForPbt(Tree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!);
            Assert.That(processor.Process(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts,
                NullBlockTracer.Instance)?.Hash, Is.EqualTo(block.Hash));
        }
        if (!expectPbt) return;
        foreach (string name in names)
        {
            bool binary = expected[name].GetProperty("binary").GetBoolean();
            Assert.That(() => Telemetry.GetShadowRoot(blocks[name].Hash!), Is.EqualTo(binary ? null : PbtRoot(name)).After(30_000, 50), name);
        }
    }

    /// <summary>Asserts every account of the fixture's reference allocation for <paramref name="name"/> through <see cref="Reader"/>.</summary>
    public void AssertAllocation(string name)
    {
        BlockHeader header = blocks[name].Header;
        IStateReader reader = Reader;
        foreach ((Address address, GethGenesisAllocJson account) in Eip8347FixtureState.LoadAllocation(fixtureDirectory, name))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetBalance(header, address), Is.EqualTo(account.Balance), $"{name} balance {address}");
                Assert.That(reader.GetNonce(header, address), Is.EqualTo(account.Nonce ?? 0), $"{name} nonce {address}");
                Assert.That(reader.GetCode(header, address), Is.EqualTo(account.Code ?? []), $"{name} code {address}");
            }
            if (account.Storage is not null)
                foreach ((UInt256 slot, byte[] value) in account.Storage)
                    Assert.That(reader.GetStorage(header, address, slot), Is.EqualTo(new UInt256(value, isBigEndian: true)), $"{name} slot {address}/{slot}");
        }
    }

    public static async Task<MigrationLifecycleHarness> Create(string targetPath, bool portable, FlatLayout layout, Action<ContainerBuilder>? configure = null, bool migration = true)
    {
        string fixtureDirectory = Path.Combine(Eip8347FixtureState.Directory, "builder-predeploys");
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
