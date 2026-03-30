// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using NUnit.Framework;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;
using Nethermind.Serialization.Json;
using System.Reflection;
using System.Text.Json;

namespace Ethereum.Test.Base;

public abstract class BlockchainTestBase
{
    private static readonly ILogger _logger;
    private static readonly ILogManager _logManager = new TestLogManager(LogLevel.Warn);
    private static DifficultyCalculatorWrapper DifficultyCalculator { get; }
    private const int _genesisProcessingTimeoutMs = 30000;

    static BlockchainTestBase()
    {
        DifficultyCalculator = new DifficultyCalculatorWrapper();
        _logger = _logManager.GetClassLogger();
    }

    private class DifficultyCalculatorWrapper : IDifficultyCalculator
    {
        public IDifficultyCalculator? Wrapped { get; set; }

        public UInt256 Calculate(BlockHeader header, BlockHeader parent)
        {
            if (Wrapped is null)
            {
                throw new InvalidOperationException(
                    $"Cannot calculate difficulty before the {nameof(Wrapped)} calculator is set.");
            }

            return Wrapped.Calculate(header, parent);
        }
    }

    protected async Task<EthereumTestResult> RunTest(BlockchainTest test, Stopwatch? stopwatch = null, bool failOnInvalidRlp = true, ITestBlockTracer? tracer = null)
    {
        _logger.Info($"Running {test.Name}, Network: [{test.Network!.Name}] at {DateTime.UtcNow:HH:mm:ss.ffffff}");
        if (test.NetworkAfterTransition is not null)
            _logger.Info($"Network after transition: [{test.NetworkAfterTransition.Name}] at {test.TransitionForkActivation}");
        Assert.That(test.LoadFailure, Is.Null, "test data loading failure");

        test.Network = ChainUtils.ResolveSpec(test.Network, test.ChainId);
        test.NetworkAfterTransition = ChainUtils.ResolveSpec(test.NetworkAfterTransition, test.ChainId);

        bool isEngineTest = test.Blocks is null && test.EngineNewPayloads is not null;

        // EIP-7928 introduces BlockAccessListHash in the block header, which must be computed
        // during genesis processing. Without target fork rules at genesis, the hash field is missing
        // and the genesis block header doesn't match the pyspec fixture expectation.
        bool genesisUsesTargetFork = test.Network.IsEip7928Enabled;

        List<(ForkActivation Activation, IReleaseSpec Spec)> transitions = genesisUsesTargetFork
            ? [((ForkActivation)0, test.Network)]
            : [((ForkActivation)0, test.GenesisSpec), ((ForkActivation)1, test.Network)]; // genesis block is always initialized with Frontier

        if (test.NetworkAfterTransition is not null)
        {
            transitions.Add((test.TransitionForkActivation!.Value, test.NetworkAfterTransition));
        }

        ISpecProvider specProvider = new CustomSpecProvider(test.ChainId, test.ChainId, transitions.ToArray());


        if (test.Network.IsEip4844Enabled || test.NetworkAfterTransition?.IsEip4844Enabled == true)
        {
            KzgPolynomialCommitments.Initialize();
        }

        DifficultyCalculator.Wrapped = new EthashDifficultyCalculator(specProvider);
        IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);
        bool isPostMerge = test.Network != London.Instance &&
                           test.Network != Berlin.Instance &&
                           test.Network != MuirGlacier.Instance &&
                           test.Network != Istanbul.Instance &&
                           test.Network != ConstantinopleFix.Instance &&
                           test.Network != Constantinople.Instance &&
                           test.Network != Byzantium.Instance &&
                           test.Network != SpuriousDragon.Instance &&
                           test.Network != TangerineWhistle.Instance &&
                           test.Network != Dao.Instance &&
                           test.Network != Homestead.Instance &&
                           test.Network != Frontier.Instance &&
                           test.Network != Olympic.Instance;
        if (isPostMerge)
        {
            rewardCalculator = NoBlockRewards.Instance;
            specProvider.UpdateMergeTransitionInfo(0, 0);
        }

        IConfigProvider configProvider = new ConfigProvider();
        IBlocksConfig blocksConfig = configProvider.GetConfig<IBlocksConfig>();
        blocksConfig.PreWarmStateConcurrency = 0;
        blocksConfig.PreWarmStateOnBlockProcessing = false;
        ContainerBuilder containerBuilder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton(specProvider)
            .AddSingleton(_logManager)
            .AddSingleton(rewardCalculator)
            .AddSingleton<IDifficultyCalculator>(DifficultyCalculator)
            .AddSingleton<ITxPool>(NullTxPool.Instance);

        if (isEngineTest)
        {
            containerBuilder.AddModule(new TestMergeModule(configProvider));
        }

        await using IContainer container = containerBuilder.Build();

        IMainProcessingContext mainBlockProcessingContext = container.Resolve<IMainProcessingContext>();
        IWorldState stateProvider = mainBlockProcessingContext.WorldState;
        BlockchainProcessor blockchainProcessor = (BlockchainProcessor)mainBlockProcessingContext.BlockchainProcessor;
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        IBlockValidator blockValidator = container.Resolve<IBlockValidator>();
        blockchainProcessor.Start();

        if (tracer is not null)
        {
            blockchainProcessor.Tracers.Add(tracer);
        }

        try
        {
            BlockHeader parentHeader;
            // Genesis processing
            using (stateProvider.BeginScope(null))
            {
                InitializeTestState(test, stateProvider, specProvider);

                stopwatch?.Start();

                test.GenesisRlp ??= Rlp.Encode(new Block(JsonToEthereumTest.Convert(test.GenesisBlockHeader)));

                Block genesisBlock = Rlp.Decode<Block>(test.GenesisRlp.Bytes);
                Assert.That(genesisBlock.Header.Hash, Is.EqualTo(new Hash256(test.GenesisBlockHeader.Hash)));

                using ManualResetEvent genesisProcessed = new(false);
                EventHandler<BlockEventArgs> onNewHeadBlock = (_, args) =>
                {
                    if (args.Block.Number == 0)
                    {
                        Assert.That(stateProvider.HasStateForBlock(genesisBlock.Header), Is.True);
                        genesisProcessed.Set();
                    }
                };
                EventHandler<BlockRemovedEventArgs> onGenesisBlockRemoved = (_, args) =>
                {
                    if (args.ProcessingResult != ProcessingResult.Success && args.BlockHash == genesisBlock.Header.Hash)
                    {
                        Assert.Fail($"Failed to process genesis block: {args.Exception}");
                        genesisProcessed.Set();
                    }
                };

                blockTree.NewHeadBlock += onNewHeadBlock;
                blockchainProcessor.BlockRemoved += onGenesisBlockRemoved;

                try
                {
                    blockTree.SuggestBlock(genesisBlock);
                    Assert.That(genesisProcessed.WaitOne(_genesisProcessingTimeoutMs), Is.True,
                        "Timed out waiting for genesis block processing.");
                    parentHeader = genesisBlock.Header;
                }
                finally
                {
                    blockTree.NewHeadBlock -= onNewHeadBlock;
                    blockchainProcessor.BlockRemoved -= onGenesisBlockRemoved;
                }

                genesisBlock.DisposeAccountChanges();
            }

            if (test.Blocks is not null)
            {
                // blockchain test
                parentHeader = SuggestBlocks(test, failOnInvalidRlp, blockValidator, blockTree, parentHeader);
            }
            else if (test.EngineNewPayloads is not null)
            {
                // engine test — route through JsonRpcService for realistic deserialization
                IJsonRpcService rpcService = container.Resolve<IJsonRpcService>();
                JsonRpcUrl engineUrl = new(Uri.UriSchemeHttp, "localhost", 8551, RpcEndpoint.Http, true, ["engine"]);
                JsonRpcContext rpcContext = new(RpcEndpoint.Http, url: engineUrl);
                await RunNewPayloads(test.EngineNewPayloads, rpcService, rpcContext, parentHeader.Hash!);
            }
            else
            {
                Assert.Fail("Invalid blockchain test, did not contain blocks or new payloads.");
            }

            // NOTE: Tracer removal must happen AFTER StopAsync to ensure all blocks are traced
            // Blocks are queued asynchronously, so we need to wait for processing to complete
            await blockchainProcessor.StopAsync(true);
            stopwatch?.Stop();

            IBlockCachePreWarmer? preWarmer = container.Resolve<MainProcessingContext>().LifetimeScope.ResolveOptional<IBlockCachePreWarmer>();

            // Caches are cleared async, which is a problem as read for the MainWorldState with prewarmer is not correct if its not cleared.
            preWarmer?.ClearCaches();

            Block? headBlock = blockTree.RetrieveHeadBlock();

            Assert.That(headBlock, Is.Not.Null);
            if (headBlock is null)
            {
                return new EthereumTestResult(test.Name, null, false);
            }

            List<string> differences;
            using (stateProvider.BeginScope(headBlock.Header))
            {
                differences = RunAssertions(test, headBlock, stateProvider);
            }

            bool testPassed = differences.Count == 0;

            // Write test end marker if using streaming tracer (JSONL format)
            // This must be done BEFORE removing tracer and BEFORE Assert to ensure marker is written even on failure
            if (tracer is not null)
            {
                tracer.TestFinished(test.Name, testPassed, test.Network, stopwatch?.Elapsed, headBlock?.StateRoot);
                blockchainProcessor.Tracers.Remove(tracer);
            }

            Assert.That(differences, Is.Empty, "differences");
            return new EthereumTestResult(test.Name, null, testPassed);
        }
        catch (Exception)
        {
            await blockchainProcessor.StopAsync(true);
            throw;
        }
    }

    private static BlockHeader SuggestBlocks(BlockchainTest test, bool failOnInvalidRlp, IBlockValidator blockValidator, IBlockTree blockTree, BlockHeader parentHeader)
    {
        List<(Block Block, string ExpectedException)> correctRlp = DecodeRlps(test, failOnInvalidRlp);
        for (int i = 0; i < correctRlp.Count; i++)
        {
            // Mimic the actual behaviour where block goes through validating sync manager
            correctRlp[i].Block.Header.IsPostMerge = correctRlp[i].Block.Difficulty == 0;

            // For tests with reorgs, find the actual parent header from block tree
            parentHeader = blockTree.FindHeader(correctRlp[i].Block.ParentHash) ?? parentHeader;

            Assert.That(correctRlp[i].Block.Hash, Is.Not.Null, $"null hash in {test.Name} block {i}");

            bool expectsException = correctRlp[i].ExpectedException is not null;
            // Validate block structure first (mimics SyncServer validation)
            if (blockValidator.ValidateSuggestedBlock(correctRlp[i].Block, parentHeader, out string? validationError))
            {
                Assert.That(!expectsException, $"Expected block {correctRlp[i].Block.Hash} to fail with '{correctRlp[i].ExpectedException}', but it passed validation");
                try
                {
                    // All validations passed, suggest the block
                    blockTree.SuggestBlock(correctRlp[i].Block);

                }
                catch (InvalidBlockException e)
                {
                    // Exception thrown during block processing
                    Assert.That(expectsException, $"Unexpected invalid block {correctRlp[i].Block.Hash}: {validationError}, Exception: {e}");
                    // else: Expected to fail and did fail via exception → this is correct behavior
                }
                catch (Exception e)
                {
                    Assert.Fail($"Unexpected exception during processing: {e}");
                }
                finally
                {
                    // Dispose AccountChanges to prevent memory leaks in tests
                    correctRlp[i].Block.DisposeAccountChanges();
                }
            }
            else
            {
                // Validation FAILED
                Assert.That(expectsException, $"Unexpected invalid block {correctRlp[i].Block.Hash}: {validationError}");
                // else: Expected to fail and did fail → this is correct behavior
            }

            parentHeader = correctRlp[i].Block.Header;
        }
        return parentHeader;
    }

    private static readonly Dictionary<int, int> s_newPayloadParamCounts = Enumerable
        .Range(1, EngineApiVersions.NewPayload.Latest)
        .ToDictionary(v => v, v => (typeof(IEngineRpcModule).GetMethod($"engine_newPayloadV{v}")
            ?? throw new NotSupportedException($"engine_newPayloadV{v} not found on IEngineRpcModule")).GetParameters().Length);

    private async static Task RunNewPayloads(TestEngineNewPayloadsJson[]? newPayloads, IJsonRpcService rpcService, JsonRpcContext rpcContext, Hash256 initialHeadHash)
    {
        if (newPayloads is null || newPayloads.Length == 0) return;

        int initialFcuVersion = int.Parse(newPayloads[0].ForkChoiceUpdatedVersion ?? EngineApiVersions.Fcu.Latest.ToString());
        AssertRpcSuccess(await SendFcu(rpcService, rpcContext, initialFcuVersion, initialHeadHash.ToString()));

        foreach (TestEngineNewPayloadsJson enginePayload in newPayloads)
        {
            int newPayloadVersion = int.Parse(enginePayload.NewPayloadVersion ?? EngineApiVersions.NewPayload.Latest.ToString());
            int fcuVersion = int.Parse(enginePayload.ForkChoiceUpdatedVersion ?? EngineApiVersions.Fcu.Latest.ToString());
            string? validationError = JsonToEthereumTest.ParseValidationError(enginePayload, newPayloadVersion);

            int paramCount = s_newPayloadParamCounts[newPayloadVersion];
            string paramsJson = "[" + string.Join(",", enginePayload.Params.Take(paramCount).Select(static p => p.GetRawText())) + "]";

            JsonRpcResponse npResponse = await SendRpc(rpcService, rpcContext,
                "engine_newPayloadV" + newPayloadVersion, paramsJson);

            // RPC-level errors (e.g. wrong payload version) are valid for negative tests
            if (npResponse is JsonRpcErrorResponse errorResponse)
            {
                Assert.That(validationError, Is.Not.Null,
                    $"engine_newPayloadV{newPayloadVersion} RPC error: {errorResponse.Error?.Code} {errorResponse.Error?.Message}");
                continue;
            }

            PayloadStatusV1 payloadStatus = (PayloadStatusV1)((JsonRpcSuccessResponse)npResponse).Result!;
            string expectedStatus = validationError is null ? PayloadStatus.Valid : PayloadStatus.Invalid;
            Assert.That(payloadStatus.Status, Is.EqualTo(expectedStatus),
                $"engine_newPayloadV{newPayloadVersion} returned {payloadStatus.Status}, expected {expectedStatus}. " +
                $"ValidationError: {payloadStatus.ValidationError}");

            if (payloadStatus.Status == PayloadStatus.Valid)
            {
                string blockHash = enginePayload.Params[0].GetProperty("blockHash").GetString()!;
                AssertRpcSuccess(await SendFcu(rpcService, rpcContext, fcuVersion, blockHash));
            }
        }
    }

    private static async Task<JsonRpcResponse> SendRpc(IJsonRpcService rpcService, JsonRpcContext context, string method, string paramsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(paramsJson);
        JsonRpcRequest request = new() { JsonRpc = "2.0", Id = 1, Method = method, Params = doc.RootElement.Clone() };
        return await rpcService.SendRequestAsync(request, context);
    }

    private static Task<JsonRpcResponse> SendFcu(IJsonRpcService rpcService, JsonRpcContext context, int fcuVersion, string blockHash) =>
        SendRpc(rpcService, context, "engine_forkchoiceUpdatedV" + fcuVersion,
            $$$"""[{"headBlockHash":"{{{blockHash}}}","safeBlockHash":"{{{blockHash}}}","finalizedBlockHash":"{{{blockHash}}}"},null]""");

    private static void AssertRpcSuccess(JsonRpcResponse response)
    {
        Assert.That(response, Is.InstanceOf<JsonRpcSuccessResponse>(),
            response is JsonRpcErrorResponse err ? $"RPC error: {err.Error?.Code} {err.Error?.Message}" : "unexpected response type");
    }

    private static List<(Block Block, string ExpectedException)> DecodeRlps(BlockchainTest test, bool failOnInvalidRlp)
    {
        List<(Block Block, string ExpectedException)> correctRlp = [];
        for (int i = 0; i < test.Blocks!.Length; i++)
        {
            TestBlockJson testBlockJson = test.Blocks[i];
            try
            {
                byte[] rlpBytes = Bytes.FromHexString(testBlockJson.Rlp!);
                Block suggestedBlock = Rlp.Decode<Block>(rlpBytes);

                if (testBlockJson.BlockHeader is not null)
                {
                    Assert.That(suggestedBlock.Header.Hash, Is.EqualTo(new Hash256(testBlockJson.BlockHeader.Hash)));

                    for (int uncleIndex = 0; uncleIndex < suggestedBlock.Uncles.Length; uncleIndex++)
                    {
                        Assert.That(suggestedBlock.Uncles[uncleIndex].Hash, Is.EqualTo(new Hash256(testBlockJson.UncleHeaders![uncleIndex].Hash)));
                    }

                    correctRlp.Add((suggestedBlock, testBlockJson.ExpectException));
                }
            }
            catch (Exception e)
            {
                if (testBlockJson.ExpectException is null)
                {
                    string invalidRlpMessage = $"Invalid RLP ({i}) {e}";
                    Assert.That(!failOnInvalidRlp, invalidRlpMessage);
                    // ForgedTests don't have ExpectedException and at the same time have invalid rlps
                    // Don't fail here. If test executed incorrectly will fail at last check
                    _logger.Warn(invalidRlpMessage);
                }
                else
                {
                    _logger.Info($"Expected invalid RLP ({i})");
                }
            }
        }

        if (correctRlp.Count == 0)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(test.GenesisBlockHeader, Is.Not.Null);
                Assert.That(test.LastBlockHash, Is.EqualTo(new Hash256(test.GenesisBlockHeader.Hash)));
            }
        }

        return correctRlp;
    }

    private static void InitializeTestState(BlockchainTest test, IWorldState stateProvider, ISpecProvider specProvider)
    {
        foreach (KeyValuePair<Address, AccountState> accountState in
            (IEnumerable<KeyValuePair<Address, AccountState>>)test.Pre ?? Array.Empty<KeyValuePair<Address, AccountState>>())
        {
            foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Value.Storage)
            {
                stateProvider.Set(new StorageCell(accountState.Key, storageItem.Key), storageItem.Value);
            }

            stateProvider.CreateAccount(accountState.Key, accountState.Value.Balance, accountState.Value.Nonce);
            stateProvider.InsertCode(accountState.Key, accountState.Value.Code, specProvider.GenesisSpec);
        }

        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);
        stateProvider.Reset();
    }

    private static List<string> RunAssertions(BlockchainTest test, Block headBlock, IWorldState stateProvider)
    {
        if (test.PostStateRoot is not null)
        {
            return test.PostStateRoot != stateProvider.StateRoot ? ["state root mismatch"] : Enumerable.Empty<string>().ToList();
        }

        List<string> differences = [];

        IEnumerable<KeyValuePair<Address, AccountState>> deletedAccounts = test.Pre?
            .Where(pre => !(test.PostState?.ContainsKey(pre.Key) ?? false)) ?? Array.Empty<KeyValuePair<Address, AccountState>>();

        foreach (KeyValuePair<Address, AccountState> deletedAccount in deletedAccounts)
        {
            if (stateProvider.AccountExists(deletedAccount.Key))
            {
                differences.Add($"Pre state account {deletedAccount.Key} was not deleted as expected.");
            }
        }

        foreach ((Address accountAddress, AccountState accountState) in test.PostState!)
        {
            int differencesBefore = differences.Count;

            if (differences.Count > 8)
            {
                Console.WriteLine("More than 8 differences...");
                break;
            }

            bool accountExists = stateProvider.AccountExists(accountAddress);
            UInt256? balance = accountExists ? stateProvider.GetBalance(accountAddress) : null;
            UInt256? nonce = accountExists ? stateProvider.GetNonce(accountAddress) : null;

            if (accountState.Balance != balance)
            {
                differences.Add($"{accountAddress} balance exp: {accountState.Balance}, actual: {balance}, diff: {(balance > accountState.Balance ? balance - accountState.Balance : accountState.Balance - balance)}");
            }

            if (accountState.Nonce != nonce)
            {
                differences.Add($"{accountAddress} nonce exp: {accountState.Nonce}, actual: {nonce}");
            }

            byte[] code = accountExists ? stateProvider.GetCode(accountAddress) : [];
            if (!Bytes.AreEqual(accountState.Code, code))
            {
                differences.Add($"{accountAddress} code exp: {accountState.Code?.Length}, actual: {code?.Length}");
            }

            if (differences.Count != differencesBefore)
            {
                _logger.Info($"ACCOUNT STATE ({accountAddress}) HAS DIFFERENCES");
            }

            differencesBefore = differences.Count;

            KeyValuePair<UInt256, byte[]>[] clearedStorages = [];
            if (test.Pre.ContainsKey(accountAddress))
            {
                clearedStorages = [.. test.Pre[accountAddress].Storage.Where(s => !accountState.Storage.ContainsKey(s.Key))];
            }

            foreach (KeyValuePair<UInt256, byte[]> clearedStorage in clearedStorages)
            {
                ReadOnlySpan<byte> value = !stateProvider.AccountExists(accountAddress) ? Bytes.Empty : stateProvider.Get(new StorageCell(accountAddress, clearedStorage.Key));
                if (!value.IsZero())
                {
                    differences.Add($"{accountAddress} storage[{clearedStorage.Key}] exp: 0x00, actual: {value.ToHexString(true)}");
                }
            }

            foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Storage)
            {
                ReadOnlySpan<byte> value = !stateProvider.AccountExists(accountAddress) ? Bytes.Empty : stateProvider.Get(new StorageCell(accountAddress, storageItem.Key));
                if (!Bytes.AreEqual(storageItem.Value, value))
                {
                    differences.Add($"{accountAddress} storage[{storageItem.Key}] exp: {storageItem.Value.ToHexString(true)}, actual: {value.ToHexString(true)}");
                }
            }

            if (differences.Count != differencesBefore)
            {
                _logger.Info($"ACCOUNT STORAGE ({accountAddress}) HAS DIFFERENCES");
            }
        }

        TestBlockHeaderJson? testHeaderJson = test.Blocks?
                                                 .Where(b => b.BlockHeader is not null)
                                                 .SingleOrDefault(b => new Hash256(b.BlockHeader.Hash) == headBlock.Hash)?.BlockHeader;

        if (testHeaderJson is not null)
        {
            BlockHeader testHeader = JsonToEthereumTest.Convert(testHeaderJson);

            BigInteger gasUsed = headBlock.Header.GasUsed;
            if ((testHeader?.GasUsed ?? 0) != gasUsed)
            {
                differences.Add($"GAS USED exp: {testHeader?.GasUsed ?? 0}, actual: {gasUsed}");
            }

            if (headBlock.Transactions.Length != 0 && testHeader.Bloom.ToString() != headBlock.Header.Bloom.ToString())
            {
                differences.Add($"BLOOM exp: {testHeader.Bloom}, actual: {headBlock.Header.Bloom}");
            }

            if (testHeader.StateRoot != stateProvider.StateRoot)
            {
                differences.Add($"STATE ROOT exp: {testHeader.StateRoot}, actual: {stateProvider.StateRoot}");
            }

            if (testHeader.TxRoot != headBlock.Header.TxRoot)
            {
                differences.Add($"TRANSACTIONS ROOT exp: {testHeader.TxRoot}, actual: {headBlock.Header.TxRoot}");
            }

            if (testHeader.ReceiptsRoot != headBlock.Header.ReceiptsRoot)
            {
                differences.Add($"RECEIPT ROOT exp: {testHeader.ReceiptsRoot}, actual: {headBlock.Header.ReceiptsRoot}");
            }
        }

        if (test.LastBlockHash != headBlock.Hash)
        {
            differences.Add($"LAST BLOCK HASH exp: {test.LastBlockHash}, actual: {headBlock.Hash}");
        }

        foreach (string difference in differences)
        {
            _logger.Info(difference);
        }

        return differences;
    }
}
