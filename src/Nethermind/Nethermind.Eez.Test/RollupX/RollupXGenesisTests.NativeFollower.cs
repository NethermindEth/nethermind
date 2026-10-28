// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Consensus.Processing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Evm.State;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Proofs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [Test]
    public void FromGenesis_CurrentNativeProfile_SelectsCompleteV6Rules()
    {
        EezChainRules rules = EezChainRules.FromGenesisFile(StatelessFixtures.PathOf("rollupx/native-offline-v6", "l2-genesis.json"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.NativeFormat, Is.EqualTo(NativeSystemTxFormat.ContractV6));
            Assert.That(rules.UsesNativeContract, Is.True);
            Assert.That(rules.FeeCollector, Is.Not.Null);
        }
    }

    [Test]
    public void FromGenesis_PreviousNativeProfile_RequiresAnExplicitHistoricalLoader()
    {
        string path = StatelessFixtures.PathOf("rollupx/native-offline-v4", "l2-genesis.json");
        Assert.That(() => EezChainRules.FromGenesisFile(path), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => ChainConfigLoader.LoadProfile(path), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    [NonParallelizable]
    public async Task NativeFollower_RecordedHistoryChoice_DerivesAndExecutesThroughProductionScopes([Values] bool import,
        [Values] bool proxyHistory)
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        NativeSystemTxFormat format = proxyHistory ? NativeSystemTxFormat.ContractV6 : NativeSystemTxFormat.ContractV4;
        EezChainRules rules = FollowerRules(source, format);
        using IContainer container = NativeFollowerContainer(source, rules);
        BlockHeader parent = NativeFollowerParent(container, source, rules);
        NativeHistoryEnvelope envelope = format == NativeSystemTxFormat.ContractV6
            ? NativeHistoryEnvelope.CreateBlocks(1, [new(1, source.Headers[1].Hash!.ValueHash256)])
            : NativeHistoryEnvelope.Create(1, [source.Headers[1].Hash!.ValueHash256]);
        ReadOnlyMemory<byte>[] ordinary = import ? [envelope.Encode()] : [];
        byte[] operations = OperationsPayload.Encode([
            new OperationsRun(1, Address.Zero, ReadOnlyMemory<byte>.Empty, ordinary),
            new OperationsRun(5, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        DecodedBatch batch = container.Resolve<IBatchDerivation>().Decode(NativeStream.Encode(1, operations, []));
        L1Anchor settling = source.Anchor(2) with { ParentHash = source.Headers[2].ParentHash };
        DerivedBlock[] derived = await batch.DeriveAsync(new ProducingSlice(0, 0),
            new DerivationInputs(0, parent.MixHash!, parent, settling, false) { ExecutionParent = parent }, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(derived.Length, Is.EqualTo(6));
            foreach (DerivedBlock block in derived)
                if (proxyHistory)
                    Assert.That(block.NativeOriginFromParent, Is.True, "upgradeable history is read from each actual parent during execution");
                else
                    Assert.That(block.PrevRandao, Is.EqualTo(source.Headers[import ? 1UL : 0UL].MixHash),
                        "the follower must retain the recorded optional import instead of recomputing producer choice");
            Assert.That(container.Resolve<IDerivedBlockExecutor>(), Is.TypeOf<NativeDerivedBlockExecutor>());
        }
        TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(format));
        try
        {
            using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
            Block executed = await session.ExecuteAsync(parent, derived[0]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(executed.MixHash, Is.EqualTo(source.Headers[import ? 1UL : 0UL].MixHash));
                Assert.That(executed.Transactions.Length, Is.EqualTo(import ? 1 : 0));
                Assert.That(executed.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(executed.Transactions)));
                if (import)
                    Assert.That(TxDecoder.Instance.Encode(executed.Transactions[0], RlpBehaviors.SkipTypedWrapping).Bytes,
                        Is.EqualTo(ordinary[0].ToArray()), "execution must preserve the original dedicated history wire envelope");
            }
        }
        finally { TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder()); }
    }

    [Test]
    [NonParallelizable]
    public async Task NativeFollower_UpgradedHistoryDoesNotStoreImport_NextBlockReadsActualParent()
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezChainRules rules = FollowerRules(source, NativeSystemTxFormat.ContractV6);
        using IContainer container = NativeFollowerContainer(source, rules);
        BlockHeader parent = NativeFollowerParent(container, source, rules, implementation: "no-op", number: 6);
        Transaction app = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithGasLimit(100_000).WithValue(0)
            .WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[] history = NativeHistoryEnvelope.CreateBlocks(1, [new(1, source.Headers[1].Hash!.ValueHash256)]).Encode();
        byte[] appBytes = TxDecoder.Instance.Encode(app, RlpBehaviors.SkipTypedWrapping).Bytes;
        byte[] operations = OperationsPayload.Encode([
            new OperationsRun(1, Address.Zero, ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>[] { history }),
            new OperationsRun(1, Address.Zero, ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>[] { appBytes }),
            new OperationsRun(4, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        DecodedBatch batch = container.Resolve<IBatchDerivation>().Decode(NativeStream.Encode(1, operations, []));
        DerivedBlock[] derived = await batch.DeriveAsync(new ProducingSlice(0, 0),
            new DerivationInputs(0, parent.MixHash!, parent, source.Anchor(3) with { ParentHash = source.Headers[3].ParentHash }, false)
            { ExecutionParent = parent }, CancellationToken.None);
        TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(NativeSystemTxFormat.ContractV6));
        try
        {
            using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
            Block first = await session.ExecuteAsync(parent, derived[0]);
            Assert.That(first.MixHash, Is.EqualTo(source.Headers[1].MixHash), "the recorded import selects this block's origin even when storage stays empty");
            PersistFollowerBlock(container, parent, first);
            container.Resolve<IStateReader>().GetStorage(first.Header, EezConstants.L1HistoryAddress, (UInt256)1, out UInt256 endpoint);
            Assert.That(endpoint, Is.Zero, "precondition: the upgraded implementation did not publish the requested endpoint");
            Block second = await session.ExecuteAsync(first.Header, derived[1]);
            UInt256 observed = PersistFollowerBlock(container, first.Header, second);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.MixHash, Is.EqualTo(source.Headers[0].MixHash), "the next block inherits the real empty store's bootstrap origin");
                Assert.That(observed.ToValueHash(), Is.EqualTo(source.Headers[0].MixHash!.ValueHash256), "application execution uses the actual inherited origin");
            }
        }
        finally { TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder()); }
    }

    [Test]
    [NonParallelizable]
    public async Task NativeFollower_UpgradeResetsHistoryBeforeRetainedRange_ExpandsEvidenceBeforeNextExecution()
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezChainRules rules = FollowerRules(source, NativeSystemTxFormat.ContractV6);
        using IContainer container = NativeFollowerContainer(source, rules);
        BlockHeader parent = NativeFollowerParent(container, source, rules, implementation: "reset", importedEndpoint: 1, number: 6);
        Transaction reset = Build.A.Transaction.WithChainId(1).WithTo(EezConstants.L1HistoryAddress).WithNonce(0).WithValue(0)
            .WithGasLimit(100_000).WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction app = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithNonce(1).WithValue(0)
            .WithGasLimit(100_000).WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[] operations = OperationsPayload.Encode([
            new OperationsRun(1, Address.Zero, ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>[] { TxDecoder.Instance.Encode(reset, RlpBehaviors.SkipTypedWrapping).Bytes }),
            new OperationsRun(1, Address.Zero, ReadOnlyMemory<byte>.Empty, new ReadOnlyMemory<byte>[] { TxDecoder.Instance.Encode(app, RlpBehaviors.SkipTypedWrapping).Bytes }),
            new OperationsRun(4, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        DecodedBatch batch = container.Resolve<IBatchDerivation>().Decode(NativeStream.Encode(1, operations, []));
        L1Anchor anchor = source.Anchor(3) with { ParentHash = source.Headers[3].ParentHash };
        DerivedBlock[] derived = await batch.DeriveAsync(new ProducingSlice(0, 0),
            new DerivationInputs(0, parent.MixHash!, parent, anchor, false) { ExecutionParent = parent }, CancellationToken.None);
        NativeCanonicalHistory history = container.Resolve<NativeCanonicalHistory>();
        await history.Prepare(1, source.Anchor(5), CancellationToken.None);
        NativeL1HeaderChain borrowed = history.Read();
        List<ulong> reads = [];
        source.OnRead = reads.Add;
        Assert.That(borrowed.FirstNumber, Is.EqualTo(1UL), "precondition: genesis history is outside the retained range");
        TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder(NativeSystemTxFormat.ContractV6));
        try
        {
            using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
            Block first = await session.ExecuteAsync(parent, derived[0]);
            Assert.That(first.MixHash, Is.EqualTo(source.Headers[1].MixHash));
            PersistFollowerBlock(container, parent, first);
            Block second = await session.ExecuteAsync(first.Header, derived[1]);
            UInt256 observed = PersistFollowerBlock(container, first.Header, second);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.MixHash, Is.EqualTo(source.Headers[0].MixHash));
                Assert.That(observed.ToValueHash(), Is.EqualTo(source.Headers[0].MixHash!.ValueHash256));
                Assert.That(history.Read().FirstNumber, Is.Zero);
                Assert.That(history.Read().Endpoint.Hash, Is.EqualTo(source.Headers[5].Hash!.ValueHash256), "global readiness preserves checked descendants");
                Assert.That(reads, Does.Contain(anchor.Number).And.Not.Contain(5UL), "expansion binds the batch anchor instead of the newer ready endpoint");
                Assert.That(borrowed.FirstNumber, Is.EqualTo(1UL), "the borrowed execution snapshot stays immutable");
            }
        }
        finally { TxDecoder.Instance.RegisterDecoder(new EezNativeHistoryTxDecoder()); }
    }

    private static UInt256 PersistFollowerBlock(IContainer container, BlockHeader parent, Block block)
    {
        IMainProcessingContext main = container.Resolve<IMainProcessingContext>();
        using IDisposable scope = main.WorldState.BeginScope(parent);
        Block processed = main.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Osaka.Instance).Block;
        Assert.That(processed.Hash, Is.EqualTo(block.Hash), "the real node replay must agree with derivation");
        main.WorldState.CommitTree(block.Number);
        main.WorldState.Get(new StorageCell(TestItem.AddressB, UInt256.Zero), out UInt256 observed);
        return observed;
    }

    [Test]
    public void EnsureRolesSupported_NativeFollowerWithoutSourceConfig_RefusesToStart([Values] bool follower)
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezConfig config = new() { FollowerEnabled = follower };
        Assert.That(() => EezPlugin.EnsureRolesSupported(config, FollowerRules(source)),
            Throws.TypeOf<InvalidConfigurationException>());
    }

    [Test]
    public void NativeFollower_UnsafeSource_RefusesUnavailablePreparation()
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezConfig config = new()
        {
            FollowerEnabled = true,
            L1ChainConfigPath = "l1.json",
            L1ChainId = 1337,
            L1BeaconUrl = "http://beacon.test/",
            SequencerRpcUrl = "http://sequencer.test/"
        };
        Assert.That(() => EezPlugin.EnsureRolesSupported(config, FollowerRules(source)),
            Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(nameof(IEezConfig.SequencerRpcUrl)));
    }

    [Test]
    public void NativeFollower_BatchOverResourceBudget_RejectsBeforeL1Reads()
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        using IContainer container = NativeFollowerContainer(source, FollowerRules(source));
        byte[] operations = OperationsPayload.Encode([new OperationsRun(16386, Address.Zero, ReadOnlyMemory<byte>.Empty, [])]);
        Assert.That(() => container.Resolve<IBatchDerivation>().Decode(NativeStream.Encode(1, operations, [])),
            Throws.TypeOf<L1SourceIncompleteException>().With.Message.Contains(nameof(IEezConfig.MaxFollowerBatchBlocks)));
        Assert.That(source.Methods, Is.Empty);
    }

    [Test]
    [NonParallelizable]
    public async Task NativeFollower_ExplicitTemplate_ResolvesGasAndCommitmentsWhileRawInputRejects()
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezChainRules rules = FollowerRules(source);
        using IContainer container = NativeFollowerContainer(source, rules);
        BlockHeader parent = NativeFollowerParent(container, source, rules);
        await container.Resolve<NativeCanonicalHistory>().Prepare(0,
            source.Anchor(2) with { ParentHash = source.Headers[2].ParentHash }, CancellationToken.None);
        byte[] calldata = new byte[350000];
        calldata.AsSpan().Fill(1);
        Transaction ordinary = Build.A.Transaction.WithChainId(1).WithTo(Address.FromNumber(987)).WithValue(0)
            .WithData(calldata).WithGasLimit(14100000).WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction native = new()
        {
            Type = EezConstants.NativeSystemTxType,
            ChainId = 1,
            SenderAddress = Address.SystemUser,
            SourceHash = new Hash256(new NativeLiveSource(1, source.Headers[1].Hash!.ValueHash256).Entry(0).Bytes),
            To = rules.Eezl2Address,
            GasLimit = EezChainRules.RollupXSystemTxGasCap,
            Data = EezCalldata.EncodeExecuteIncomingCrossChainCall(new IncomingCrossChainCall([], []))
        };
        TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder(rules));
        try
        {
            byte[] template = TxDecoder.Instance.Encode(native, RlpBehaviors.SkipTypedWrapping).Bytes;
            DerivedBlock derived = new(Address.Zero, [], [TxDecoder.Instance.Encode(ordinary, RlpBehaviors.SkipTypedWrapping).Bytes, template])
            { PrevRandao = parent.MixHash!, NativeEffectsStart = 1 };
            IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().CreateResettableWorldState();
            using (ILifetimeScope raw = container.BeginLifetimeScope(b => b.AddScoped(provider)))
                Assert.That(() => container.Resolve<DerivedBlockBuilder>().Build(parent, derived,
                    raw.Resolve<IBlockProcessor>(), raw.Resolve<IWorldState>()), Throws.TypeOf<EezSettlementException>(),
                    "raw validation must reject a wire budget larger than the actual remaining block gas");
            using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
            Block block = await session.ExecuteAsync(parent, derived);
            byte[] final = TxDecoder.Instance.Encode(block.Transactions[1], RlpBehaviors.SkipTypedWrapping).Bytes;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(block.Transactions[0].BlockGasUsed, Is.EqualTo(14021000UL), "Osaka calldata floor: 21000 + 350000 * 40");
                Assert.That(block.Transactions[1].GasLimit, Is.EqualTo(15979000UL), "native budget: 30000000 - 14021000");
                Assert.That(block.Transactions[1].Hash, Is.EqualTo(Keccak.Compute(final)), "receipt hashing uses the final envelope");
                Assert.That(block.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(block.Transactions)));
                Assert.That(TxDecoder.Instance.Decode(template, RlpBehaviors.SkipTypedWrapping)!.GasLimit,
                    Is.EqualTo(EezChainRules.RollupXSystemTxGasCap), "the producer's template buffer remains unchanged");
            }
        }
        finally { TxDecoder.Instance.RegisterDecoder(new EezNativeSystemTxDecoder()); }
    }

    [Test]
    public void NativeFollower_GenesisBootstrap_RequiresNoReadyL1History([Values] bool hasTransactions)
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezChainRules rules = FollowerRules(source);
        using IContainer container = NativeFollowerContainer(source, rules);
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().CreateResettableWorldState();
        using ILifetimeScope scope = container.BeginLifetimeScope(b => b.AddScoped(provider));
        IWorldState state = scope.Resolve<IWorldState>();
        using IDisposable stateScope = state.BeginScope(IWorldState.PreGenesis);
        BlockHeader header = Build.A.BlockHeader.WithNumber(0).WithTimestamp(rules.Schedule!.GenesisTimestamp)
            .WithGasLimit(EezChainRules.RollupXBlockGasLimit).TestObject;
        Transaction[] transactions = hasTransactions
            ? [new NativeHistoryTransaction(NativeHistoryEnvelope.Create(1, [source.Headers[1].Hash!.ValueHash256]))]
            : [];
        Block genesis = new(header, transactions, []);
        using BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(genesis);
        IBlockProcessor.IBlockTransactionsExecutor executor = scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>();
        executor.SetBlockExecutionContext(new BlockExecutionContext(header, Osaka.Instance));
        Assert.That(() => container.Resolve<NativeCanonicalHistory>().Read(), Throws.TypeOf<L1SourceIncompleteException>(),
            "precondition: the follower cannot prepare L1 history before genesis loading");
        if (hasTransactions)
            Assert.That(() => executor.ProcessTransactions(genesis, ProcessingOptions.None, tracer), Throws.TypeOf<InvalidBlockException>());
        else
            Assert.That(executor.ProcessTransactions(genesis, ProcessingOptions.None, tracer), Is.Empty);
        Assert.That(source.Methods, Is.Empty, "genesis must load without network I/O or circular follower startup");
    }

    private static EezChainRules FollowerRules(NativeHistoryTestSource source, NativeSystemTxFormat format = NativeSystemTxFormat.ContractV4)
    {
        BlockHeaderForRpc genesis = source.Headers[0];
        RollupXSchedule schedule = new(2, 6, new RollupXL1Reference(1337, 0, genesis.Hash!, (ulong)genesis.Timestamp, genesis.MixHash!),
            (ulong)genesis.Timestamp + 12);
        return EezChainRules.NativeRollupX(schedule, Address.FromNumber(123), format);
    }

    private static IContainer NativeFollowerContainer(NativeHistoryTestSource source, EezChainRules rules)
    {
        EezConfig config = new()
        {
            Enabled = true,
            FollowerEnabled = true,
            L1RpcUrl = "http://l1.test/",
            L1BeaconUrl = "http://beacon.test/",
            L1ChainId = 1337,
            L1ChainConfigPath = StatelessFixtures.PathOf(L1Capture, "l1-genesis.json"),
            RegistryAddress = Address.FromNumber(456).ToString(),
            RegistryDeployBlock = 1,
            RollupId = 1
        };
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .AddSingleton<IEezConfig>(config)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddModule(new EezModule(config, rules));
        builder.RegisterInstance(source.Http).Keyed<HttpClient>(EezModule.NativeL1ClientKey).ExternallyOwned();
        return builder.Build();
    }

    private static BlockHeader NativeFollowerParent(IContainer container, NativeHistoryTestSource source, EezChainRules rules, string? implementation = null, ulong? importedEndpoint = null, ulong number = 0)
    {
        IWorldState state = container.Resolve<IMainProcessingContext>().WorldState;
        Hash256 root;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether);
            if (rules.NativeFormat == NativeSystemTxFormat.ContractV6)
            {
                NativeHistoryFixtures.InstallProxy(state);
                if (implementation is not null)
                {
                    byte[] code = implementation == "reset" ? [0x5f, 0x60, 0x01, 0x55, 0x00] : [0x00];
                    state.InsertCode(NativeProxyHistory.ImplementationAddress, code, Osaka.Instance);
                    state.CreateAccount(TestItem.AddressB, 0, 1);
                    state.InsertCode(TestItem.AddressB, new byte[] { (byte)Instruction.PREVRANDAO, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE, (byte)Instruction.STOP }, Osaka.Instance);
                }
            }
            else
            {
                state.CreateAccount(EezConstants.L1HistoryAddress, 0, 1);
                state.InsertCode(EezConstants.L1HistoryAddress, File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-offline-v4", "history-runtime.bin")), Osaka.Instance);
                NativeHistorySchedule schedule = new(rules.Schedule!.GenesisTimestamp, 2, 6, 0);
                ulong[] slots = [0, 0, schedule.FirstCutoff, schedule.Interval, schedule.Delay, 64];
                for (int index = 0; index < slots.Length; index++)
                    state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)(index + 1)), slots[index]);
                state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(0)), source.Headers[0].Hash!.ValueHash256.ToUInt256());
            }
            if (importedEndpoint is { } endpoint)
            {
                state.Set(new StorageCell(EezConstants.L1HistoryAddress, UInt256.One), endpoint);
                state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(endpoint)), source.Headers[endpoint].Hash!.ValueHash256.ToUInt256());
            }
            state.Commit(Osaka.Instance, isGenesis: number == 0);
            state.CommitTree(number);
            root = state.StateRoot;
        }
        BlockHeader parent = Build.A.BlockHeader.WithNumber(number).WithTimestamp(rules.Schedule!.Timestamp(number)!.Value)
            .WithGasLimit(EezChainRules.RollupXBlockGasLimit).WithMixHash(source.Headers[importedEndpoint ?? 0].MixHash!).WithStateRoot(root).TestObject;
        parent.BaseFeePerGas = 1;
        parent.Hash = Keccak.Compute(Rlp.Encode(parent).Bytes);
        return parent;
    }
}
