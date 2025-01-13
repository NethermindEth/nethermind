// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Network.Config;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.Test.Modules;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

[TestFixture(NodeMode.Default)]
[TestFixture(NodeMode.Hash)]
[TestFixture(NodeMode.NoPruning)]
public class E2ESyncTests(E2ESyncTests.NodeMode mode)
{
    public enum NodeMode
    {
        Default,
        Hash,
        NoPruning
    }

    private static TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);
    private static TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    PrivateKey _serverKey = TestItem.PrivateKeyA;
    IContainer _server = null!;

    /// <summary>
    /// Common code for all node
    /// </summary>
    private IContainer CreateNode(PrivateKey nodeKey, Action<IConfigProvider, ChainSpec> configurer)
    {
        IConfigProvider configProvider = new ConfigProvider();
        ChainSpec spec = new ChainSpecLoader(new EthereumJsonSerializer()).LoadEmbeddedOrFromFile("chainspec/foundation.json", default);

        // Set basefeepergas in genesis or it will fail 1559 validation.
        spec.Genesis.Header.BaseFeePerGas = 1.GWei();

        // Disable as the built block always don't have withdrawal (it came from engine) so it fail validation.
        spec.Parameters.Eip4895TransitionTimestamp = null;

        // 4844 add BlobGasUsed which in the header decoder also imply WithdrawalRoot which would be set to 0 instead of null
        // which become invalid when using block body with null withdrawal.
        // Basically, these need merge block builder, or it will fail block validation on download.
        spec.Parameters.Eip4844TransitionTimestamp = null;

        // Always on, as the timestamp based fork activation always override block number based activation. However, the receipt
        // message serializer does not check the block header of the receipt for timestamp, only block number therefore it will
        // always not encode with Eip658, but the block builder always build with Eip658 as the latest fork activation
        // uses timestamp which is < than now.
        // TODO: Need to double check which code part does not pass in timestamp from header.
        spec.Parameters.Eip658Transition = 0;

        // Needed for generating spam state.
        spec.Genesis.Header.GasLimit = 100000000;
        spec.Allocations[_serverKey.Address] = new ChainSpecAllocation(30.Ether());

        configurer.Invoke(configProvider, spec);

        switch (mode)
        {
            case NodeMode.Default:
                // Um... nothing?
                break;
            case NodeMode.Hash:
            {
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                initConfig.StateDbKeyScheme = INodeStorage.KeyScheme.Hash;
                break;
            }
            case NodeMode.NoPruning:
            {
                IPruningConfig pruningConfig = configProvider.GetConfig<IPruningConfig>();
                pruningConfig.Mode = PruningMode.None;
                break;
            }
        }

        return new ContainerBuilder()
            .AddModule(new PsudoNethermindModule(configProvider, spec))
            .AddModule(new TestEnvironmentModule(nodeKey))
            .Build();
    }

    [OneTimeSetUp]
    public async Task SetupServer()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(SetupTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey serverKey = TestItem.PrivateKeyA;
        _serverKey = serverKey;
        _server = CreateNode(serverKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = 1000;
        });

        var serverCtx = _server.Resolve<BlockchainTestContext>();
        await serverCtx.StartBlockProcessing(cancellationToken);

        byte[] spam = Prepare.EvmCode
            .ForCreate2Of(
                Prepare.EvmCode
                    .PushData(100)
                    .PushData(100)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .PushData(101)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .PushData(102)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .PushData(103)
                    .Op(Instruction.SSTORE)
                    .PushData(100)
                    .Op(Instruction.SLOAD)
                    .PushData(101)
                    .Op(Instruction.SLOAD)
                    .PushData(102)
                    .Op(Instruction.SLOAD)
                    .PushData(103)
                    .Op(Instruction.SLOAD)
                    .Done)
            .Done;

        for (int i = 0; i < 1000; i++)
        {
            await serverCtx.BuildBlockWithCode([spam, spam, spam], cancellationToken);
        }

        await serverCtx.StartNetwork(cancellationToken);
    }

    [OneTimeTearDown]
    public async Task TearDownServer()
    {
        await _server.DisposeAsync();
    }


    [Test]
    public async Task FullSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TestTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey clientKey = TestItem.PrivateKeyB;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = 1001;
        });

        var clientCtx = client.Resolve<BlockchainTestContext>();
        await clientCtx.StartBlockProcessing(cancellationToken);
        await clientCtx.StartNetwork(cancellationToken);

        await clientCtx.ConnectTo(_server, cancellationToken);
        await clientCtx.SyncUntilFinished(cancellationToken);
        await clientCtx.VerifyHeadWith(_server, cancellationToken);
    }

    [Test]
    public async Task FastSync()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TestTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey clientKey = TestItem.PrivateKeyB;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig) cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - 500)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = 1002;
        });

        var clientCtx = client.Resolve<BlockchainTestContext>();
        await clientCtx.StartBlockProcessing(cancellationToken);
        await clientCtx.StartNetwork(cancellationToken);

        await clientCtx.ConnectTo(_server, cancellationToken);
        await clientCtx.SyncUntilFinished(cancellationToken);
        await clientCtx.VerifyHeadWith(_server, cancellationToken);
    }

    [Test]
    public async Task SnapSync()
    {
        if (mode == NodeMode.Hash) Assert.Ignore("Hash db does not support snap sync");

        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TestTimeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        PrivateKey clientKey = TestItem.PrivateKeyB;
        await using IContainer client = CreateNode(clientKey, (cfg, spec) =>
        {
            SyncConfig syncConfig = (SyncConfig) cfg.GetConfig<ISyncConfig>();
            syncConfig.FastSync = true;
            syncConfig.SnapSync = true;

            IBlockTree serverBlockTree = _server.Resolve<IBlockTree>();
            long serverHeadNumber = serverBlockTree.Head!.Number;
            BlockHeader pivot = serverBlockTree.FindHeader(serverHeadNumber - 500)!;
            syncConfig.PivotHash = pivot.Hash!.ToString();
            syncConfig.PivotNumber = pivot.Number.ToString();
            syncConfig.PivotTotalDifficulty = pivot.TotalDifficulty!.Value.ToString();

            INetworkConfig networkConfig = cfg.GetConfig<INetworkConfig>();
            networkConfig.P2PPort = 1002;
        });

        var clientCtx = client.Resolve<BlockchainTestContext>();
        await clientCtx.StartBlockProcessing(cancellationToken);
        await clientCtx.StartNetwork(cancellationToken);

        await clientCtx.ConnectTo(_server, cancellationToken);
        await clientCtx.SyncUntilFinished(cancellationToken);
        await clientCtx.VerifyHeadWith(_server, cancellationToken);
    }

    /*
    private async Task<ProcessingResult?> ValidateBlockAndProcess(IBlockProcessingQueue blockProcessingQueue, IBlockTree blockTree, Block block, ProcessingOptions processingOptions)
    {
        ProcessingResult? result = null;

        TaskCompletionSource<ProcessingResult?> blockProcessedTaskCompletionSource = new();
        Task<ProcessingResult?> blockProcessed = blockProcessedTaskCompletionSource.Task;

        void GetProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs e)
        {
            if (e.BlockHash == block.Hash)
            {
                blockProcessingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;

                if (e.ProcessingResult == ProcessingResult.Exception)
                {
                    BlockchainException? exception = new("Block processing threw exception.", e.Exception);
                    blockProcessedTaskCompletionSource.SetException(exception);
                    return;
                }

                blockProcessedTaskCompletionSource.TrySetResult(e.ProcessingResult);
            }
        }

        blockProcessingQueue.BlockRemoved += GetProcessingQueueOnBlockRemoved;
        try
        {
            Task timeoutTask = Task.Delay(1.Seconds());

            AddBlockResult addResult = await blockTree
                .SuggestBlockAsync(block, BlockTreeSuggestOptions.ForceDontSetAsMain)
                .AsTask().TimeoutOn(timeoutTask);

            result = addResult switch
            {
                AddBlockResult.InvalidBlock => ProcessingResult.ProcessingError,
                // if the block is marked as AlreadyKnown by the block tree then it means it has already
                // been suggested. there are three possibilities, either the block hasn't been processed yet,
                // the block was processed and returned invalid but this wasn't saved anywhere or the block was
                // processed and marked as valid.
                // if marked as processed by the blocktree then return VALID, otherwise null so that it's process a few lines below
                AddBlockResult.AlreadyKnown => blockTree.WasProcessed(block.Number, block.Hash!) ? ProcessingResult.ProcessingError : null,
                _ => null
            };

            if (!result.HasValue)
            {
                // we don't know the result of processing the block, either because
                // it is the first time we add it to the tree or it's AlreadyKnown in
                // the tree but hasn't yet been processed. if it's the second case
                // probably the block is already in the processing queue as a result
                // of a previous newPayload or the block being discovered during syncing
                // but add it to the processing queue just in case.
                blockProcessingQueue.Enqueue(block, processingOptions);
                result = await blockProcessed.TimeoutOn(timeoutTask);
            }
        }
        finally
        {
            blockProcessingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;
        }

        return result;
    }
    */

}
