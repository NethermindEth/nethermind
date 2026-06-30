// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.BalRecorder.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class BalRecorderE2ETests
{
    private const int BlocksToBuild = 3;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private static readonly BlockAccessListDecoder BalDecoder = BlockAccessListDecoder.Instance;

    [Test]
    public async Task Record_Then_Replay_RoundTrip()
    {
        using CancellationTokenSource cts = new CancellationTokenSource().ThatCancelAfter(TestTimeout);
        string dir = Path.Combine(Path.GetTempPath(), $"bal-e2e-{Guid.NewGuid():N}");
        try
        {
            List<(ulong Number, byte[] EncodedBal)> recorded;
            await using (IContainer recorder = CreateNode(dir, recording: true, replay: false))
            {
                BlockBuilder builder = recorder.Resolve<BlockBuilder>();
                await builder.StartAndBuildBlocks(BlocksToBuild, cts.Token);
                recorded = CaptureRecordedBals(recorder, BlocksToBuild);
                Assert.That(recorded, Has.Count.EqualTo(BlocksToBuild));
                Assert.That(Directory.GetFiles(dir, "*.bal"), Is.Not.Empty);
            }

            await using IContainer replayContainer = CreateNode(dir, recording: false, replay: true);
            IRecordedBalStore store = replayContainer.Resolve<IRecordedBalStore>();
            foreach ((ulong number, byte[] expected) in recorded)
            {
                ReadOnlyBlockAccessList? reread = store.Get(number);
                Assert.That(reread, Is.Not.Null);
                using ArrayPoolSpan<byte> reencoded = BalDecoder.EncodeToArrayPoolSpan(reread!);
                Assert.That(((ReadOnlySpan<byte>)reencoded).ToArray(), Is.EqualTo(expected));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static List<(ulong, byte[])> CaptureRecordedBals(IContainer container, int count)
    {
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        IRecordedBalStore store = container.Resolve<IRecordedBalStore>();
        List<(ulong, byte[])> result = [];
        for (ulong i = 1; i <= (ulong)count; i++)
        {
            Block? block = blockTree.FindBlock(i);
            Assert.That(block, Is.Not.Null);
            ReadOnlyBlockAccessList? bal = store.Get(block!.Number);
            Assert.That(bal, Is.Not.Null, $"block {i} should have a recorded BAL");
            using ArrayPoolSpan<byte> encoded = BalDecoder.EncodeToArrayPoolSpan(bal!);
            result.Add((block.Number, ((ReadOnlySpan<byte>)encoded).ToArray()));
        }
        return result;
    }

    private static IContainer CreateNode(string balDir, bool recording, bool replay)
    {
        IConfigProvider configProvider = new ConfigProvider();
        ChainSpec spec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/foundation.json");

        spec.Genesis.Header.BaseFeePerGas = 10.Wei;
        spec.Genesis.Header.GasLimit = 1_000_000_000;
        spec.Genesis.Header.Difficulty = 10000;
        spec.Allocations[TestItem.PrivateKeyA.Address] = new ChainSpecAllocation(300.Ether);
        spec.Allocations[Eip7002Constants.WithdrawalRequestPredeployAddress] = new ChainSpecAllocation
        {
            Code = Eip7002TestConstants.Code,
            Nonce = Eip7002TestConstants.Nonce
        };
        spec.Allocations[Eip7251Constants.ConsolidationRequestPredeployAddress] = new ChainSpecAllocation
        {
            Code = Eip7251TestConstants.Code,
            Nonce = Eip7251TestConstants.Nonce
        };

        ActivateAllBlockTransitionsFromGenesis(spec);

        IMergeConfig mergeConfig = configProvider.GetConfig<IMergeConfig>();
        mergeConfig.Enabled = true;
        mergeConfig.TerminalTotalDifficulty = "10000";
        mergeConfig.FinalTotalDifficulty = "10000";

        IBalRecorderConfig balConfig = configProvider.GetConfig<IBalRecorderConfig>();
        balConfig.RecordingEnabled = recording;
        balConfig.ReplayEnabled = replay;
        balConfig.Path = balDir;

        ManualTimestamper timestamper = new(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        return new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, $"bal-e2e-{Guid.NewGuid():N}"))
            .AddModule(new TestMergeModule(configProvider))
            .AddModule(new BalRecorderModule())
            .AddSingleton(timestamper)
            .AddSingleton<BlockBuilder>()
            .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Info))
            .Build();
    }

    private sealed class BlockBuilder(
        [KeyFilter(TestEnvironmentModule.NodeKey)] PrivateKey nodeKey,
        ISpecProvider specProvider,
        IEthereumEcdsa ecdsa,
        IBlockTree blockTree,
        ITxPool txPool,
        ManualTimestamper timestamper,
        IPayloadPreparationService payloadPreparationService,
        PseudoNethermindRunner runner)
    {
        private ulong _nonce;

        public async Task StartAndBuildBlocks(int count, CancellationToken token)
        {
            await runner.StartBlockProcessing(token);
            for (int i = 0; i < count; i++) await BuildOne(token);
        }

        private async Task BuildOne(CancellationToken token)
        {
            IReleaseSpec spec = specProvider.GetSpec((blockTree.Head?.Number ?? 0) + 1, null);
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithValue(1.Ether)
                .WithNonce(_nonce++)
                .WithGasLimit(21_000)
                .WithGasPrice(10.GWei)
                .SignedAndResolved(ecdsa, nodeKey, spec.IsEip155Enabled).TestObject;

            Task newBlock = blockTree.WaitForNewBlock(token);
            Assert.That(txPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            timestamper.Add(TimeSpan.FromSeconds(1));

            string? payloadId = payloadPreparationService.StartPreparingPayload(
                blockTree.Head!.Header,
                new PayloadAttributes
                {
                    PrevRandao = Hash256.Zero,
                    SuggestedFeeRecipient = TestItem.AddressA,
                    Withdrawals = [],
                    ParentBeaconBlockRoot = Hash256.Zero,
                    Timestamp = (ulong)timestamper.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds
                });
            Assert.That(payloadId, Is.Not.Null.And.Not.Empty);

            IBlockProductionContext? ctx = await payloadPreparationService.GetPayload(payloadId!, skipCancel: true);
            Assert.That(ctx, Is.Not.Null);
            Assert.That((await blockTree.SuggestBlockAsync(ctx!.CurrentBestBlock!)), Is.EqualTo(AddBlockResult.Added));
            await newBlock;
        }
    }

    private static void RekeyDictionaryToGenesis(IDictionary<ulong, ulong>? dict)
    {
        if (dict is null or { Count: 0 }) return;
        ulong total = 0;
        foreach (ulong v in dict.Values)
        {
            total += v;
        }
        dict.Clear();
        dict[0] = total;
    }

    private static void RekeyBlockRewardToGenesis(SortedDictionary<ulong, UInt256>? dict)
    {
        if (dict is null or { Count: 0 }) return;
        UInt256 lastReward = dict.Values.Last();
        dict.Clear();
        dict[0] = lastReward;
    }

    private static void ActivateAllBlockTransitionsFromGenesis(ChainSpec spec)
    {
        spec.HomesteadBlockNumber = 0;
        spec.DaoForkBlockNumber = null;
        spec.TangerineWhistleBlockNumber = 0;
        spec.SpuriousDragonBlockNumber = 0;
        spec.ByzantiumBlockNumber = 0;
        spec.ConstantinopleFixBlockNumber = 0;
        spec.IstanbulBlockNumber = 0;
        spec.BerlinBlockNumber = 0;
        spec.LondonBlockNumber = 0;
        spec.ArrowGlacierBlockNumber = 0;
        spec.GrayGlacierBlockNumber = 0;
        ActivateAllParameterTransitionsFromGenesis(spec.Parameters);
        ActivateAllEthashTransitionsFromGenesis(spec);
    }

    private static void ActivateAllParameterTransitionsFromGenesis(ChainParameters parameters)
    {
        parameters.MaxCodeSizeTransition = 0;
        parameters.Eip150Transition = 0;
        parameters.Eip152Transition = 0;
        parameters.Eip160Transition = 0;
        parameters.Eip161abcTransition = 0;
        parameters.Eip161dTransition = 0;
        parameters.Eip155Transition = 0;
        parameters.Eip140Transition = 0;
        parameters.Eip211Transition = 0;
        parameters.Eip214Transition = 0;
        parameters.Eip658Transition = 0;
        parameters.Eip145Transition = 0;
        parameters.Eip1014Transition = 0;
        parameters.Eip1052Transition = 0;
        parameters.Eip1108Transition = 0;
        parameters.Eip1344Transition = 0;
        parameters.Eip1884Transition = 0;
        parameters.Eip2028Transition = 0;
        parameters.Eip2200Transition = 0;
        parameters.Eip2565Transition = 0;
        parameters.Eip2929Transition = 0;
        parameters.Eip2930Transition = 0;
        parameters.Eip1559Transition = 0;
        parameters.Eip3198Transition = 0;
        parameters.Eip3529Transition = 0;
        parameters.Eip3541Transition = 0;
    }

    private static void ActivateAllEthashTransitionsFromGenesis(ChainSpec spec)
    {
        EthashChainSpecEngineParameters ethashParams =
            spec.EngineChainSpecParametersProvider.GetChainSpecParameters<EthashChainSpecEngineParameters>();
        ethashParams.HomesteadTransition = 0;
        ethashParams.DaoHardforkTransition = null;
        ethashParams.Eip100bTransition = 0;
        RekeyDictionaryToGenesis(ethashParams.DifficultyBombDelays);
        RekeyBlockRewardToGenesis(ethashParams.BlockReward);
    }
}
