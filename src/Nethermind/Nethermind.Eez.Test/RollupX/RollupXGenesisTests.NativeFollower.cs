// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
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
    public void FromGenesis_CurrentNativeProfile_SelectsCompleteV4Rules()
    {
        EezChainRules rules = EezChainRules.FromGenesisFile(StatelessFixtures.PathOf("rollupx/native-offline-v4", "l2-genesis.json"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.NativeFormat, Is.EqualTo(NativeSystemTxFormat.ContractV4));
            Assert.That(rules.UsesNativeContract, Is.True);
            Assert.That(rules.FeeCollector, Is.Not.Null);
        }
    }

    [Test]
    [NonParallelizable]
    public async Task NativeFollower_RecordedHistoryChoice_DerivesAndExecutesThroughProductionScopes([Values] bool import)
    {
        using NativeHistoryTestSource source = CreateHistorySource();
        EezChainRules rules = FollowerRules(source);
        using IContainer container = NativeFollowerContainer(source, rules);
        BlockHeader parent = NativeFollowerParent(container, source, rules);
        ReadOnlyMemory<byte>[] ordinary = import
            ? [NativeHistoryEnvelope.Create(1, [source.Headers[1].Hash!.ValueHash256]).Encode()]
            : [];
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
                Assert.That(block.PrevRandao, Is.EqualTo(source.Headers[import ? 1UL : 0UL].MixHash),
                    "the follower must retain the recorded optional import instead of recomputing producer choice");
            Assert.That(container.Resolve<IDerivedBlockExecutor>(), Is.TypeOf<NativeDerivedBlockExecutor>());
        }
        using IDerivedBlockSession session = container.Resolve<IDerivedBlockExecutor>().BeginSession();
        Block executed = session.Execute(parent, derived[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(executed.MixHash, Is.EqualTo(derived[0].PrevRandao));
            Assert.That(executed.Transactions.Length, Is.EqualTo(import ? 1 : 0));
            Assert.That(executed.Header.TxRoot, Is.EqualTo(TxTrie.CalculateRoot(executed.Transactions)));
            if (import)
                Assert.That(TxDecoder.Instance.Encode(executed.Transactions[0], RlpBehaviors.SkipTypedWrapping).Bytes,
                    Is.EqualTo(ordinary[0].ToArray()), "execution must preserve the original dedicated history wire envelope");
        }
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
            Block block = session.Execute(parent, derived);
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

    private static EezChainRules FollowerRules(NativeHistoryTestSource source)
    {
        BlockHeaderForRpc genesis = source.Headers[0];
        RollupXSchedule schedule = new(2, 6, new RollupXL1Reference(1337, 0, genesis.Hash!, (ulong)genesis.Timestamp, genesis.MixHash!),
            (ulong)genesis.Timestamp + 12);
        return EezChainRules.NativeRollupX(schedule, Address.FromNumber(123), NativeSystemTxFormat.ContractV4);
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

    private static BlockHeader NativeFollowerParent(IContainer container, NativeHistoryTestSource source, EezChainRules rules)
    {
        IWorldStateScopeProvider provider = container.Resolve<IWorldStateManager>().CreateResettableWorldState();
        using ILifetimeScope scope = container.BeginLifetimeScope(b => b.AddScoped(provider));
        IWorldState state = scope.Resolve<IWorldState>();
        Hash256 root;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether);
            state.CreateAccount(EezConstants.L1HistoryAddress, 0, 1);
            state.InsertCode(EezConstants.L1HistoryAddress, File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-offline-v4", "history-runtime.bin")), Osaka.Instance);
            NativeHistorySchedule schedule = new(rules.Schedule!.GenesisTimestamp, 2, 6, 0);
            ulong[] slots = [0, 0, schedule.FirstCutoff, schedule.Interval, schedule.Delay, 64];
            for (int index = 0; index < slots.Length; index++)
                state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)(index + 1)), slots[index]);
            state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(0)), source.Headers[0].Hash!.ValueHash256.ToUInt256());
            state.Commit(Osaka.Instance);
            state.CommitTree(0);
            root = state.StateRoot;
        }
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithTimestamp(rules.Schedule!.GenesisTimestamp)
            .WithGasLimit(EezChainRules.RollupXBlockGasLimit).WithMixHash(source.Headers[0].MixHash!).WithStateRoot(root).TestObject;
        parent.BaseFeePerGas = 1;
        parent.Hash = Keccak.Compute(Rlp.Encode(parent).Bytes);
        return parent;
    }
}
