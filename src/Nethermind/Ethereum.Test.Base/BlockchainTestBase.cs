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
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin;
using Nethermind.JsonRpc;

namespace Ethereum.Test.Base;

public abstract class BlockchainTestBase
{
    private static readonly ILogger _logger;
    private static readonly ILogManager _logManager = new TestLogManager();
    private static DifficultyCalculatorWrapper DifficultyCalculator { get; }

    static BlockchainTestBase()
    {
        DifficultyCalculator = new DifficultyCalculatorWrapper();
        _logManager ??= LimboLogs.Instance;
        _logger = _logManager.GetClassLogger();
    }

    [SetUp]
    public void Setup()
    {
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

        List<(ForkActivation Activation, IReleaseSpec Spec)> transitions = [((ForkActivation)0, test.Network)];
        if (test.NetworkAfterTransition is not null)
        {
            transitions.Add((test.TransitionForkActivation!.Value, test.NetworkAfterTransition));
        }

        ISpecProvider specProvider = new CustomSpecProvider(test.ChainId, test.ChainId, transitions.ToArray());

        if (test.Network is Cancun || test.NetworkAfterTransition is Cancun)
        {
            await KzgPolynomialCommitments.InitializeAsync();
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
        // configProvider.GetConfig<IBlocksConfig>().PreWarmStateOnBlockProcessing = false;
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddModule(new TestMergeModule(configProvider))
            .AddSingleton(specProvider)
            .AddSingleton(_logManager)
            .AddSingleton(rewardCalculator)
            .AddSingleton<IDifficultyCalculator>(DifficultyCalculator)
            .AddSingleton<ITxPool>(NullTxPool.Instance)
            .Build();

        IMainProcessingContext mainBlockProcessingContext = container.Resolve<IMainProcessingContext>();
        IWorldState stateProvider = mainBlockProcessingContext.WorldState;
        IBlockchainProcessor blockchainProcessor = mainBlockProcessingContext.BlockchainProcessor;
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        IBlockValidator blockValidator = container.Resolve<IBlockValidator>();
        IEngineRpcModule engineRpcModule = container.Resolve<IEngineRpcModule>();
        blockchainProcessor.Start();

        // Register tracer if provided for blocktest tracing
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

                ManualResetEvent genesisProcessed = new(false);

                blockTree.NewHeadBlock += (_, args) =>
                {
                    if (args.Block.Number == 0)
                    {
                        Assert.That(stateProvider.HasStateForBlock(genesisBlock.Header), Is.True);
                        genesisProcessed.Set();
                    }
                };

                blockTree.SuggestBlock(genesisBlock);
                genesisProcessed.WaitOne();
                parentHeader = genesisBlock.Header;

                // Dispose genesis block's AccountChanges
                genesisBlock.DisposeAccountChanges();
            }

            if (test.Blocks is not null)
            {
                // blockchain test
                parentHeader = SuggestBlocks(test, failOnInvalidRlp, blockValidator, blockTree, parentHeader);
            }
            else if (test.EngineNewPayloads is not null)
            {
                RunNewPayloads(test.EngineNewPayloads, engineRpcModule);
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

            return new EthereumTestResult
            (
                test.Name,
                null,
                differences.Count == 0
            );
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

    private async static void RunNewPayloads(TestEngineNewPayloadsJson[]? newPayloads, IEngineRpcModule engineRpcModule)
    {
        (ExecutionPayload, string[]?, string[]?, string?)[] payloads = [.. JsonToEthereumTest.Convert(newPayloads)];

        // blockchain test engine
        foreach ((ExecutionPayload executionPayload, string[]? blobVersionedHashes, string[]? validationError, string? newPayloadVersion) in payloads)
        {
            ResultWrapper<PayloadStatusV1> res;
            byte[]?[] hashes = blobVersionedHashes is null ? null : [.. blobVersionedHashes.Select(x => Bytes.FromHexString(x))];

            switch (newPayloadVersion ?? "5")
            {
                case "1":
                    res = await engineRpcModule.engine_newPayloadV1(executionPayload);
                    break;
                case "2":
                    res = await engineRpcModule.engine_newPayloadV2(executionPayload);
                    break;
                case "3":
                    res = await engineRpcModule.engine_newPayloadV3((ExecutionPayloadV3)executionPayload, [], executionPayload.ParentBeaconBlockRoot);
                    break;
                case "4":
                    res = await engineRpcModule.engine_newPayloadV4((ExecutionPayloadV3)executionPayload, hashes, executionPayload.ParentBeaconBlockRoot, []);
                    break;
                // case "5":
                //     res = await engineRpcModule.engine_newPayloadV5((ExecutionPayloadV3)executionPayload, hashes, executionPayload.ParentBeaconBlockRoot, []);
                //     break;
                default:
                    Assert.Fail("Invalid blockchain engine test, version not recognised.");
                    continue;
            }

            if (res.Result.ResultType == ResultType.Success)
            {
                ForkchoiceStateV1 fcuState = new(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash);
                await engineRpcModule.engine_forkchoiceUpdatedV3(fcuState);
            }
        }
    }

    private static List<(Block Block, string ExpectedException)> DecodeRlps(BlockchainTest test, bool failOnInvalidRlp)
    {
        List<(Block Block, string ExpectedException)> correctRlp = [];
        for (int i = 0; i < test.Blocks!.Length; i++)
        {
            TestBlockJson testBlockJson = test.Blocks[i];
            try
            {
                RlpStream rlpContext = Bytes.FromHexString(testBlockJson.Rlp!).AsRlpStream();
                Block suggestedBlock = Rlp.Decode<Block>(rlpContext);

                if (testBlockJson.BlockHeader is not null)
                {
                    Assert.That(suggestedBlock.Header.Hash, Is.EqualTo(new Hash256(testBlockJson.BlockHeader.Hash)));

                    for (int uncleIndex = 0; uncleIndex < suggestedBlock.Uncles.Length; uncleIndex++)
                    {
                        Assert.That(suggestedBlock.Uncles[uncleIndex].Hash, Is.EqualTo(new Hash256(testBlockJson.UncleHeaders![uncleIndex].Hash)));
                    }

                    correctRlp.Add((suggestedBlock, testBlockJson.ExpectedException));
                }
            }
            catch (Exception e)
            {
                if (testBlockJson.ExpectedException is null)
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
