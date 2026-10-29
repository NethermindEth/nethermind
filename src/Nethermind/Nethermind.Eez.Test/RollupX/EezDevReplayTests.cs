// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

/// <summary>
/// The first fourteen batches of the eez.dev testnet (Chiado, L2 chain 699069), replayed through the follower from
/// their recorded L1 blocks, logs, posting transactions and beacon blobs: every L2 block it derives must have the hash the
/// live chain has. The window spans catch-up and live settlements, an L1-to-L2 deposit at block 1950 and user
/// transactions at blocks 1968 and 1969.
/// </summary>
public class EezDevReplayTests
{
    private const string Fixture = "rollupx/eezdev-699069";
    private const ulong RollupId = 1;
    private static readonly Address Registry = new("0x96d6617629b90bdf275dd1d4888170ad5266fa33");

    private IContainer _container = null!;
    private IBlockTree _chain = null!;
    private EezFollower _follower = null!;
    private Hash256[] _expected = null!;
    private WitnessGeneratingBlockProcessingEnvFactory _witnessFactory = null!;
    private readonly Dictionary<ulong, Witness> _witnesses = [];

    [SetUp]
    public async Task SetUp()
    {
        await KzgPolynomialCommitments.InitializeAsync();
        string genesisPath = StatelessFixtures.PathOf(Fixture, "genesis.json");
        EezChainRules rules = EezChainRules.FromGenesisFile(genesisPath);
        ChainSpec chainSpec;
        using (FileStream file = File.OpenRead(genesisPath))
        {
            chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(file);
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(
                new FlatDbConfig { Enabled = TestStateBackend.UseFlatDb },
                new PruningConfig { Mode = PruningMode.None }), chainSpec, useTestSpecProvider: false))
            .AddModule(new EezModule(new EezConfig(), rules))
            .Build();
        IMainProcessingContext processing = _container.Resolve<IMainProcessingContext>();
        processing.BlockProcessingQueue.Start();
        processing.GenesisLoader.Load();
        _chain = _container.Resolve<IBlockTree>();
        _witnessFactory = new WitnessGeneratingBlockProcessingEnvFactory(_container, _container.Resolve<IWorldStateManager>(),
            _container.Resolve<IBlockValidationModule[]>(), _container.Resolve<ISpecProvider>(), LimboLogs.Instance);

        using JsonDocument replay = JsonDocument.Parse(File.ReadAllText(StatelessFixtures.PathOf(Fixture, "replay-0-2250.json")));
        RecordedL1 l1 = new(replay.RootElement, rules.Schedule!.L1Reference);
        _expected = [.. replay.RootElement.GetProperty("l2Hashes").EnumerateArray().Select(static h => new Hash256(h.GetString()!))];
        _follower = Follower(rules, l1);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _container.Resolve<IMainProcessingContext>().BlockProcessingQueue.StopAsync();
        _witnessFactory.Dispose();
        foreach (Witness witness in _witnesses.Values)
        {
            witness.Dispose();
        }
        _witnesses.Clear();
        _container.Dispose();
    }

    [Test]
    public async Task Poll_RecordedTestnetBatches_DerivesEveryLiveBlockHash()
    {
        Assert.That(_chain.Genesis!.Hash, Is.EqualTo(_expected[0]), "precondition: the genesis is the testnet's");

        await _follower.Boot(CancellationToken.None);
        while (await _follower.Poll(CancellationToken.None) == FollowerProgress.Behind)
        {
        }

        Hash256?[] derived = [.. Enumerable.Range(0, _expected.Length).Select(n => _chain.FindHeader((ulong)n, BlockTreeLookupOptions.RequireCanonical)?.Hash)];
        int firstMismatch = Array.FindIndex(derived, n => n is null);
        for (int i = 0; i < derived.Length && firstMismatch < 0; i++)
        {
            if (derived[i] != _expected[i])
            {
                firstMismatch = i;
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMismatch, Is.EqualTo(-1), firstMismatch < 0 ? "" : $"block {firstMismatch} is {derived[firstMismatch]}, not {_expected[firstMismatch]}");
            Assert.That(_follower.Heads.Safe.Number, Is.EqualTo((ulong)_expected.Length - 1), "the last batch's endpoint is safe");
        }
        EezChainRules rules = EezChainRules.FromGenesisFile(StatelessFixtures.PathOf(Fixture, "genesis.json"));
        ISpecProvider spec = _container.Resolve<ISpecProvider>();
        EezStatelessExecutor stateless = new(spec, rules, LimboLogs.Instance);
        Assert.That(_witnesses.Count, Is.EqualTo(4), "each selected block was captured while its parent was retained");
        foreach (ulong number in new ulong[] { 1, 1950, 1968, 1969 })
        {
            Block block = _chain.FindBlock(number, BlockTreeLookupOptions.RequireCanonical)!;
            Witness witness = _witnesses[number];
            EezStatelessBlockResult result = stateless.Execute([new EezStatelessBlock(Rlp.Encode(block).Bytes, witness)], [EezTransactionCheckpoint.PreExecution])[0];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Hash, Is.EqualTo(_expected[number]), $"stateless RollupX block {number} matches the independent testnet hash");
                Assert.That(result.Checkpoints[0].TransactionIndex, Is.EqualTo(EezTransactionCheckpoint.PreExecution));
            }
            StreamingBlockExecutorTests.AssertRecordedBlock(new EezStatelessBlock(Rlp.Encode(block).Bytes, witness), result, _expected[number], spec, rules);
            if (number == 1)
            {
                Block wrongProfile = Rlp.Decode<Block>(Rlp.Encode(block).Bytes)!;
                wrongProfile.Header.GasLimit--;
                Assert.That(() => stateless.Execute([new EezStatelessBlock(Rlp.Encode(wrongProfile).Bytes, witness)], []),
                    Throws.TypeOf<EezStatelessException>().With.Message.Contains("gas limit"), "an Ethereum-valid gas-limit change still violates the RollupX profile");
            }
        }
    }

    private void CaptureWitness(Block block)
    {
        if (block.Number is not (1 or 1950 or 1968 or 1969))
        {
            return;
        }

        BlockHeader parent = _chain.FindHeader(block.ParentHash!, BlockTreeLookupOptions.None)!;
        using IWitnessGeneratingBlockProcessingEnvScope scope = _witnessFactory.CreateScope();
        Witness witness = scope.Env.CreateExistingBlockWitnessCollector().GetWitnessForExistingBlock(parent, block);
        if (_witnesses.Remove(block.Number, out Witness? previous))
        {
            previous.Dispose();
        }

        _witnesses[block.Number] = witness;
    }

    private EezFollower Follower(EezChainRules rules, RecordedL1 l1)
    {
        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
        EezSettlementContext context = new(RollupId, specProvider.ChainId, Address.Zero, default, 1);
        RollupXHeaderRules headerRules = new(specProvider, rules);
        ProcessingEngine engine = new(_chain, _container.Resolve<IMainProcessingContext>().BlockProcessingQueue, CaptureWitness);
        DerivedBlockExecutor executor = new(_container, _container.Resolve<IWorldStateManager>(), new DerivedBlockBuilder(specProvider, headerRules));
        BatchReconciler reconciler = new(_chain, _container.Resolve<IStateReader>(), executor, engine, new RollupXBatchDerivation(context, rules), headerRules,
            context, LimboLogs.Instance);
        EezConfig config = new()
        {
            FollowerEnabled = true,
            L1ChainId = RollupXSchedule.ChiadoChainId,
            RegistryAddress = Registry.ToString(),
            RegistryDeployBlock = rules.Schedule!.L1Reference.Number,
            RollupId = RollupId,
            L1LogScanBlocks = 1_000_000,
        };
        RollupXBatchScanner scanner = new(l1, new RecordedBlobs(l1), Registry, RollupId, LimboLogs.Instance);
        ResumePointFinder resumePoints = new(l1, _chain, Registry, RollupId, config.RegistryDeployBlock, config.L1LogScanBlocks, rules.Schedule);
        return new EezFollower(l1, scanner, reconciler, resumePoints, NullUnsafeHeadSource.Instance, headerRules, rules, _chain, engine, config, LimboLogs.Instance);
    }

    /// <summary>
    /// L1 as the recorded batches saw it: their blocks, logs and posting transactions, the genesis reference, and a
    /// stand-in for every other block, which settles nothing.
    /// </summary>
    private sealed class RecordedL1 : IEezL1Api
    {
        private readonly Dictionary<ulong, EezL1Block> _blocks = [];
        private readonly Dictionary<ulong, (EezL1Transaction Transaction, ulong Index)> _transactions = [];
        private readonly List<EezL1Log> _logs = [];
        private readonly ulong _head;

        public RecordedL1(JsonElement replay, RollupXL1Reference reference)
        {
            _blocks[reference.Number] = new EezL1Block { Number = reference.Number, Hash = reference.Hash, Timestamp = reference.Timestamp, MixHash = reference.PrevRandao };
            foreach (JsonElement batch in replay.GetProperty("batches").EnumerateArray())
            {
                JsonElement l1Block = batch.GetProperty("l1Block");
                EezL1Block block = new()
                {
                    Number = Quantity(l1Block, "number"),
                    Hash = new Hash256(l1Block.GetProperty("hash").GetString()!),
                    ParentHash = new Hash256(l1Block.GetProperty("parentHash").GetString()!),
                    Timestamp = Quantity(l1Block, "timestamp"),
                    MixHash = new Hash256(l1Block.GetProperty("mixHash").GetString()!),
                };
                _blocks[block.Number] = block;
                JsonElement tx = batch.GetProperty("transaction");
                EezL1Transaction transaction = new()
                {
                    Hash = new Hash256(tx.GetProperty("hash").GetString()!),
                    Input = Bytes.FromHexString(tx.GetProperty("input").GetString()!),
                    BlobVersionedHashes = [.. tx.GetProperty("blobVersionedHashes").EnumerateArray().Select(static h => new Hash256(h.GetString()!))],
                };
                ulong index = Quantity(tx, "index");
                _transactions[block.Number] = (transaction, index);
                foreach (JsonElement log in batch.GetProperty("logs").EnumerateArray())
                {
                    ulong logTransaction = Quantity(log, "transactionIndex");
                    _logs.Add(new EezL1Log
                    {
                        Address = Registry,
                        Topics = [.. log.GetProperty("topics").EnumerateArray().Select(static t => new Hash256(t.GetString()!))],
                        Data = Bytes.FromHexString(log.GetProperty("data").GetString()!),
                        BlockNumber = block.Number,
                        BlockHash = block.Hash,
                        TransactionHash = logTransaction == index ? transaction.Hash : Keccak.Compute($"transaction {logTransaction} of {block.Number}"),
                        TransactionIndex = logTransaction,
                        LogIndex = Quantity(log, "logIndex"),
                    });
                }

                SlotBlobs[block.Number] = [.. batch.GetProperty("slotBlobs").EnumerateArray().Select(static b => Blob(b.GetString()!))];
            }

            _head = _blocks.Keys.Max() + 1;
        }

        public Dictionary<ulong, byte[][]> SlotBlobs { get; } = [];

        public Task<ulong?> GetChainId(CancellationToken token) => Task.FromResult<ulong?>(RollupXSchedule.ChiadoChainId);

        public Task<EezL1Block?> GetBlockByNumber(ulong number, CancellationToken token) => Task.FromResult<EezL1Block?>(number <= _head ? Block(number) : null);

        public Task<EezL1Block?> GetLatestBlock(CancellationToken token) => Task.FromResult<EezL1Block?>(Block(_head));

        public Task<EezL1Block?> GetFinalizedBlock(CancellationToken token) => Task.FromResult<EezL1Block?>(Block(_head));

        public Task<EezL1Log[]?> GetLogs(Address address, Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock, CancellationToken token) =>
            Task.FromResult<EezL1Log[]?>([.. _logs.Where(l => l.Address == address && l.Topics[0] == topic0 && (topic1 is null || (l.Topics.Length > 1 && l.Topics[1] == topic1))
                && l.BlockNumber >= fromBlock && l.BlockNumber <= toBlock)]);

        public Task<EezL1Transaction?> GetTransactionByBlockHashAndIndex(Hash256 blockHash, ulong index, CancellationToken token)
        {
            foreach (EezL1Block block in _blocks.Values)
            {
                if (block.Hash == blockHash && _transactions.TryGetValue(block.Number, out (EezL1Transaction Transaction, ulong Index) posted) && posted.Index == index)
                {
                    return Task.FromResult<EezL1Transaction?>(posted.Transaction);
                }
            }

            return Task.FromResult<EezL1Transaction?>(null);
        }

        public Task<byte[]?> Call(Address to, byte[] data, CancellationToken token) => Task.FromResult<byte[]?>(null);

        private EezL1Block Block(ulong number) =>
            _blocks.TryGetValue(number, out EezL1Block block)
                ? block
                : new EezL1Block { Number = number, Hash = Keccak.Compute($"L1 block {number}"), MixHash = Keccak.Zero };

        private static ulong Quantity(JsonElement element, string property) =>
            element.GetProperty(property) is { ValueKind: JsonValueKind.Number } number ? number.GetUInt64() : Convert.ToUInt64(element.GetProperty(property).GetString(), 16);

        private static byte[] Blob(string trimmedHex)
        {
            byte[] blob = new byte[BlobStreamBytes];
            string hex = trimmedHex[2..];
            Bytes.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0").CopyTo(blob, 0);
            return blob;
        }

        private const int BlobStreamBytes = 131_072;
    }

    /// <summary>The node's engine: a block it is given is processed by the main processor and becomes the head.</summary>
    private sealed class ProcessingEngine(IBlockTree chain, IBlockProcessingQueue queue, Action<Block> onProcessed) : IEezL2Engine
    {
        public async Task Insert(Block block)
        {
            TaskCompletionSource processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHead(object? sender, BlockEventArgs args)
            {
                if (args.Block.Hash == block.Hash)
                {
                    processed.TrySetResult();
                }
            }

            void OnInvalid(object? sender, IBlockProcessingQueue.InvalidBlockEventArgs args)
            {
                if (args.InvalidBlock.Hash == block.Hash)
                {
                    processed.TrySetException(new EezFollowerException($"The node finds block {block.Number} invalid."));
                }
            }

            chain.NewHeadBlock += OnHead;
            queue.InvalidBlock += OnInvalid;
            try
            {
                chain.SuggestBlock(block, BlockTreeSuggestOptions.ShouldProcess);
                await processed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                onProcessed(block);
            }
            finally
            {
                chain.NewHeadBlock -= OnHead;
                queue.InvalidBlock -= OnInvalid;
            }
        }

        public Task UpdateForkchoice(Hash256 head, Hash256 safe, Hash256 finalized)
        {
            chain.ForkChoiceUpdated(finalized, safe);
            return Task.CompletedTask;
        }

        public void EnsureSoleDriver()
        {
        }
    }

    /// <summary>The beacon node as the recorded slots served it.</summary>
    private sealed class RecordedBlobs(RecordedL1 l1) : IL1BlobSource
    {
        public Task<IReadOnlyDictionary<Hash256, byte[]>> GetSlotBlobs(EezL1Block block, CancellationToken token) =>
            Task.FromResult<IReadOnlyDictionary<Hash256, byte[]>>(BlobSelection.ByVersionedHash(l1.SlotBlobs[block.Number]));
    }
}
