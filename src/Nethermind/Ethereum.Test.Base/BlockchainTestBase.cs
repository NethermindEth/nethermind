// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
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
using Nethermind.Core.Exceptions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Init.Modules;
using NUnit.Framework;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;
using System.Text.Json;
using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;

namespace Ethereum.Test.Base;

public abstract class BlockchainTestBase
{
    private static readonly ILogManager _logManager = new TestLogManager(LogLevel.Warn);
    private static readonly ILogger _logger = _logManager.GetClassLogger<BlockchainTestBase>();
    private const int _genesisProcessingTimeoutMs = 30000;

    /// <summary>
    /// Override to force parallel or sequential BAL execution in tests.
    /// Null means use the default config value.
    /// </summary>
    protected virtual bool? ParallelExecutionOverride => null;

    /// <summary>
    /// Override to force BAL batch-read prewarming on or off in tests.
    /// Null means use the default config value.
    /// </summary>
    protected virtual bool? ParallelExecutionBatchReadOverride => null;

    /// <summary>
    /// Override to replace the log manager used by internal Nethermind components.
    /// Null means use the default (TestLogManager at Warn level).
    /// </summary>
    protected virtual ILogManager? ComponentLogManagerOverride => null;

    /// <summary>
    /// Whether to run under the flat state layout instead of patricia (the production default).
    /// Driven by the <c>TEST_USE_FLAT=1</c> environment variable, mirroring TestBlockchain.UseFlatDb.
    /// </summary>
    protected static bool UseFlatDb => Environment.GetEnvironmentVariable("TEST_USE_FLAT") == "1";

    protected static bool IsPostMergeSpec(IReleaseSpec spec) => spec is not NamedReleaseSpec { IsPostMerge: false };

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


        if (test.Network.IsEip4844Enabled || test.NetworkAfterTransition?.IsEip4844Enabled is true)
        {
            await KzgPolynomialCommitments.InitializeAsync();
        }

        // Per-test fresh instance bound to this test's specProvider. Tests run with
        // [Parallelizable(ParallelScope.All)] so anything shared-mutable across tests would race.
        IDifficultyCalculator difficultyCalculator = new EthashDifficultyCalculator(specProvider);
        IRewardCalculator rewardCalculator = new RewardCalculator(specProvider);
        bool isPostMerge = IsPostMergeSpec(test.Network);
        if (isPostMerge)
        {
            rewardCalculator = NoBlockRewards.Instance;
            specProvider.UpdateMergeTransitionInfo(0, 0);
        }

        IConfigProvider configProvider = new ConfigProvider();
        // Patricia by default (the production default); opt into the flat state layout with
        // TEST_USE_FLAT=1, mirroring TestBlockchain.UseFlatDb.
        configProvider.GetConfig<IFlatDbConfig>().Enabled = UseFlatDb;
        IBlocksConfig blocksConfig = configProvider.GetConfig<IBlocksConfig>();
        blocksConfig.PreWarmStateConcurrency = 0;
        blocksConfig.PreWarming = PreWarmMode.None;
        if (ParallelExecutionOverride.HasValue)
        {
            blocksConfig.ParallelExecution = ParallelExecutionOverride.Value;
        }

        if (ParallelExecutionBatchReadOverride.HasValue)
        {
            blocksConfig.ParallelExecutionBatchRead = ParallelExecutionBatchReadOverride.Value;
        }

        if (isEngineTest && configProvider.GetConfig<IMergeConfig>() is MergeConfig mergeConfig)
        {
            mergeConfig.NewPayloadBlockProcessingTimeout = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
        }

        ILogManager componentLogManager = ComponentLogManagerOverride ?? _logManager;

        ContainerBuilder containerBuilder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(configProvider))
            .AddSingleton(specProvider)
            .AddSingleton(componentLogManager)
            .AddSingleton(rewardCalculator)
            .AddSingleton<IDifficultyCalculator>(difficultyCalculator)
            // Replace NullSealEngine with a validator that enforces pre-Merge Ethash difficulty
            // matching, so legacy invalid-block fixtures (wrongDifficulty_*) are actually rejected.
            .AddSingleton<ISealValidator>(new DifficultyOnlySealValidator(difficultyCalculator))
            .AddSingleton<ITxPool>(NullTxPool.Instance)
            .AddSingleton<IWitnessGeneratingBlockProcessingEnvFactory, WitnessGeneratingBlockProcessingEnvFactory>();

        // Wire in the merge module for any post-Merge test (engine API flow OR post-Paris
        // RLP-fed blockchain test). The merge module decorates IHeaderValidator with the
        // EIP-3675 post-Merge field rules (difficulty=0, nonce=0, empty UnclesHash) which the
        // base HeaderValidator does not enforce, and which legacy invalid-block fixtures rely on.
        if (isEngineTest || isPostMerge)
        {
            containerBuilder.AddModule(new TestMergeModule());
        }

        // Seed the optional test tracer into the main processor via the BlockchainProcessor constructor.
        if (tracer is not null)
        {
            containerBuilder.AddSingleton<IBlockTracer>(tracer);
        }

        await using IContainer container = containerBuilder.Build();

        IMainProcessingContext mainBlockProcessingContext = container.Resolve<IMainProcessingContext>();
        IWorldState stateProvider = mainBlockProcessingContext.WorldState;
        BlockchainProcessor blockchainProcessor = (BlockchainProcessor)mainBlockProcessingContext.BlockchainProcessor;
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        IBlockValidator blockValidator = container.Resolve<IBlockValidator>();
        blockchainProcessor.Start();

        try
        {
            BlockHeader parentHeader;
            string lastPayloadStatus = "";
            string? lastValidationError = null;
            string? asyncBlockError = null;
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

            List<string> engineWitnessDifferences = [];
            if (test.Blocks is not null)
            {
                // blockchain test — capture async block processing errors via event
                blockchainProcessor.BlockRemoved += (_, args) =>
                {
                    if (args.ProcessingResult != ProcessingResult.Success)
                        asyncBlockError = args.Message ?? args.Exception?.Message;
                };
                Result<BlockHeader> suggestResult = SuggestBlocks(test, failOnInvalidRlp, blockValidator, blockTree, parentHeader);
                parentHeader = suggestResult.Data!;
                lastValidationError = suggestResult.Error;
            }
            else if (test.EngineNewPayloads is not null)
            {
                // engine test — route through JsonRpcService for realistic deserialization
                IJsonRpcService rpcService = container.Resolve<IJsonRpcService>();
                JsonRpcUrl engineUrl = new(Uri.UriSchemeHttp, "localhost", 8551, RpcEndpoint.Http, true, ["engine"]);
                JsonRpcContext rpcContext = new(RpcEndpoint.Http, url: engineUrl);
                Result<string> payloadResult = await RunNewPayloads(test.EngineNewPayloads, rpcService, rpcContext, parentHeader.Hash!, engineWitnessDifferences);
                lastPayloadStatus = payloadResult.Data ?? "";
                lastValidationError = payloadResult.Error;
            }
            else
            {
                Assert.Fail("Invalid blockchain test, did not contain blocks or new payloads.");
            }

            // NOTE: Tracer removal must happen AFTER StopAsync to ensure all blocks are traced
            // Blocks are queued asynchronously, so we need to wait for processing to complete
            await blockchainProcessor.StopAsync(true);
            lastValidationError ??= asyncBlockError;
            stopwatch?.Stop();

            IBlockCachePreWarmer? preWarmer = container.Resolve<MainProcessingContext>().LifetimeScope.ResolveOptional<IBlockCachePreWarmer>();

            // Caches are cleared async, which is a problem as read for the MainWorldState with prewarmer is not correct if its not cleared.
            preWarmer?.ClearCaches();

            Block? headBlock = blockTree.RetrieveHeadBlock();

            Assert.That(headBlock, Is.Not.Null);
            if (headBlock is null)
            {
                return new EthereumTestResult(test.Name, test.ForkName, false) { Error = "head block is null" };
            }

            List<string> differences;
            using (stateProvider.BeginScope(headBlock.Header))
            {
                differences = RunAssertions(test, headBlock, stateProvider);
            }

            // zkEVM witness assertions. Engine-path diffs were gathered while driving
            // engine_newPayloadWithWitness; the RLP path regenerates the witness post-hoc here.
            differences.AddRange(engineWitnessDifferences);
            if (test.Blocks is not null && HasAnyExecutionWitness(test))
            {
                IWitnessGeneratingBlockProcessingEnvFactory witnessFactory =
                    container.Resolve<IWitnessGeneratingBlockProcessingEnvFactory>();
                VerifyWitnesses(test, blockTree, witnessFactory, differences);
            }

            bool testPassed = differences.Count == 0;

            // Write test end marker if using streaming tracer (JSONL format)
            // This must be done BEFORE Assert to ensure the marker is written even on failure
            if (tracer is not null)
            {
                tracer.TestFinished(test.Name, testPassed, test.Network, stopwatch?.Elapsed, headBlock?.StateRoot);
            }

            Assert.That(differences, Is.Empty, "differences");
            return new EthereumTestResult(test.Name, test.ForkName, testPassed)
            {
                LastBlockHash = headBlock?.Hash,
                LastPayloadStatus = string.IsNullOrEmpty(lastPayloadStatus) ? null : lastPayloadStatus,
                Error = !testPassed
                    ? string.Join("; ", differences)
                    : lastValidationError,
            };
        }
        catch (Exception)
        {
            await blockchainProcessor.StopAsync(true);
            throw;
        }
    }

    /// <summary>
    /// Feeds the test's RLP blocks through validation and the block tree.
    /// </summary>
    /// <returns>
    /// The header to continue from in <see cref="Result{TData}.Data"/>; when an expected-invalid
    /// block was rejected, the rejection message in <see cref="Result{TData}.Error"/> (the header
    /// is still populated — rejection of an expected-invalid block does not fail the test).
    /// </returns>
    private static Result<BlockHeader> SuggestBlocks(BlockchainTest test, bool failOnInvalidRlp, IBlockValidator blockValidator, IBlockTree blockTree, BlockHeader parentHeader)
    {
        string? lastBlockError = null;
        List<(Block Block, string ExpectedException)> correctRlp = DecodeRlps(test, failOnInvalidRlp);
        for (int i = 0; i < correctRlp.Count; i++)
        {
            // Setting IsPostMerge here would bypass PoSSwitcher and hide divergences
            // between CI and hive's production pipeline (the bug this test path exists to catch).

            // For tests with reorgs, find the actual parent header from block tree
            parentHeader = blockTree.FindHeader(correctRlp[i].Block.ParentHash) ?? parentHeader;

            Assert.That(correctRlp[i].Block.Hash, Is.Not.Null, $"null hash in {test.Name} block {i}");

            bool expectsException = correctRlp[i].ExpectedException is not null;
            // Validate block structure first (mimics SyncServer validation). Pre-validation is
            // not authoritative for invalid-block fixtures: many EEST exceptions (state root,
            // BAL hash, BAL gas-limit floor, etc.) are only detectable post-execution. So an
            // expected-to-fail block may pass pre-validation here; rejection is then expected
            // to come from the processor (synchronous InvalidBlockException) or from the
            // end-of-test LastBlockHash check after the chain head is settled.
            if (blockValidator.ValidateSuggestedBlock(correctRlp[i].Block, parentHeader, out string? validationError))
            {
                try
                {
                    blockTree.SuggestBlock(correctRlp[i].Block);
                }
                catch (InvalidBlockException e)
                {
                    Assert.That(expectsException, $"Unexpected invalid block {correctRlp[i].Block.Hash}: {e.Message}");
                    lastBlockError = e.Message;
                }
                catch (Exception e)
                {
                    Assert.Fail($"Unexpected exception during processing: {e}");
                }
                finally
                {
                    correctRlp[i].Block.DisposeAccountChanges();
                }
            }
            else
            {
                // Header validation failed
                Assert.That(expectsException, $"Unexpected invalid block {correctRlp[i].Block.Hash}: {validationError}");
                lastBlockError = validationError;
            }

            parentHeader = correctRlp[i].Block.Header;
        }
        return lastBlockError is null
            ? Result<BlockHeader>.Success(parentHeader)
            : Result<BlockHeader>.Fail(lastBlockError, parentHeader);
    }

    private static readonly Dictionary<int, int> NewPayloadParamCounts = BuildNewPayloadParamCounts();

    private static Dictionary<int, int> BuildNewPayloadParamCounts()
    {
        Dictionary<int, int> result = [];
        for (int version = 1; version <= EngineApiVersions.NewPayload.Latest; version++)
        {
            System.Reflection.MethodInfo method = typeof(IEngineRpcModule).GetMethod($"engine_newPayloadV{version}")
                ?? throw new NotSupportedException($"engine_newPayloadV{version} not found on IEngineRpcModule. Update {nameof(EngineApiVersions.NewPayload.Latest)}.");
            result[version] = method.GetParameters().Length;
        }

        return result;
    }

    /// <summary>
    /// Replays the test's engine payloads through the JSON-RPC service.
    /// </summary>
    /// <param name="witnessDifferences">Collector for zkEVM witness mismatches found while driving engine_newPayloadWithWitness.</param>
    /// <returns>
    /// The last payload status in <see cref="Result{TData}.Data"/>; when the last INVALID payload
    /// carried a validation error, that error in <see cref="Result{TData}.Error"/> (an expected
    /// rejection does not fail the test, so the status is still populated).
    /// </returns>
    private static async Task<Result<string>> RunNewPayloads(TestEngineNewPayloadsJson[]? newPayloads, IJsonRpcService rpcService, JsonRpcContext rpcContext, Hash256 initialHeadHash, List<string> witnessDifferences)
    {
        if (newPayloads is null || newPayloads.Length == 0) return Result<string>.Success("");

        if (!int.TryParse(newPayloads[0].ForkChoiceUpdatedVersion ?? EngineApiVersions.Fcu.Latest.ToString(), out int initialFcuVersion))
            throw new FormatException($"Invalid ForkChoiceUpdatedVersion: '{newPayloads[0].ForkChoiceUpdatedVersion}'");
        AssertRpcSuccess(await SendFcu(rpcService, rpcContext, initialFcuVersion, initialHeadHash.ToString()));

        string lastStatus = "";
        string? lastValidationError = null;
        foreach (TestEngineNewPayloadsJson enginePayload in newPayloads)
        {
            if (!int.TryParse(enginePayload.NewPayloadVersion ?? EngineApiVersions.NewPayload.Latest.ToString(), out int newPayloadVersion))
                throw new FormatException($"Invalid NewPayloadVersion: '{enginePayload.NewPayloadVersion}'");
            if (!int.TryParse(enginePayload.ForkChoiceUpdatedVersion ?? EngineApiVersions.Fcu.Latest.ToString(), out int fcuVersion))
                throw new FormatException($"Invalid ForkChoiceUpdatedVersion: '{enginePayload.ForkChoiceUpdatedVersion}'");
            string? validationError = JsonToEthereumTest.ParseValidationError(enginePayload, newPayloadVersion);

            // Only an unmutated, expected-VALID reference witness exercises engine_newPayloadWithWitness; EIP-8025
            // mutated payloads go through the plain endpoint (their witness is a corrupted reference useful for stateless exec).
            bool expectWitness = enginePayload.ExecutionWitness is not null
                && enginePayload.ExecutionWitnessMutated != true
                && validationError is null;

            int paramCount = NewPayloadParamCounts[newPayloadVersion];
            IEnumerable<string> paramsRaw = enginePayload.Params.Take(paramCount).Select(static p => p.GetRawText());
            // EIP-7805 (FOCIL): the IL is a separate fixture field; append it as the 5th positional arg for V6.
            if (newPayloadVersion >= EngineApiVersions.NewPayload.V6 && enginePayload.InclusionListTransactions is { } il)
                paramsRaw = paramsRaw.Append(JsonSerializer.Serialize(il));
            string paramsJson = "[" + string.Join(",", paramsRaw) + "]";

            string npMethod = expectWitness ? "engine_newPayloadWithWitness" : "engine_newPayloadV" + newPayloadVersion;
            JsonRpcResponse npResponse = await SendRpc(rpcService, rpcContext, npMethod, paramsJson);

            // RPC-level errors (e.g. wrong payload version) are valid for negative tests
            if (TryGetRpcError(npResponse, out int errorCode, out string? errorMessage))
            {
                AssertExpectedRpcError(errorCode, errorMessage, validationError, newPayloadVersion);
            }
            else if (expectWitness)
            {
                using NewPayloadWithWitnessV1Result witnessResult = GetWitnessResult(npResponse, newPayloadVersion);
                PayloadStatusV1 payloadStatus = new() { Status = witnessResult.Status, ValidationError = witnessResult.ValidationError, LatestValidHash = witnessResult.LatestValidHash };
                AssertPayloadStatus(payloadStatus, validationError, newPayloadVersion);
                lastStatus = payloadStatus.Status;
                if (payloadStatus.ValidationError is not null)
                    lastValidationError = payloadStatus.ValidationError;

                if (payloadStatus.Status == PayloadStatus.Valid)
                {
                    Hash256 blockHash = new(enginePayload.Params[0].GetProperty("blockHash").GetString()!);
                    if (witnessResult.ExecutionWitness is null)
                    {
                        witnessDifferences.Add($"witness (block {blockHash}): engine_newPayloadWithWitness returned VALID but no witness");
                    }
                    else
                    {
                        CompareWitnesses(blockHash, enginePayload.ExecutionWitness!, witnessResult.ExecutionWitness, witnessDifferences);
                    }

                    AssertRpcSuccess(await SendFcu(rpcService, rpcContext, fcuVersion, blockHash.ToString()));
                }
            }
            else
            {
                PayloadStatusV1 payloadStatus = GetPayloadStatus(npResponse, newPayloadVersion);
                AssertPayloadStatus(payloadStatus, validationError, newPayloadVersion, enginePayload.Status);
                lastStatus = payloadStatus.Status;
                if (payloadStatus.ValidationError is not null)
                    lastValidationError = payloadStatus.ValidationError;

                // FCU after INCLUSION_LIST_UNSATISFIED too — the block is committed, so the head
                // must advance to match the fixture's lastblockhash/postState.
                if (payloadStatus.Status is PayloadStatus.Valid or PayloadStatus.InclusionListUnsatisfied)
                {
                    string blockHash = enginePayload.Params[0].GetProperty("blockHash").GetString()!;
                    AssertRpcSuccess(await SendFcu(rpcService, rpcContext, fcuVersion, blockHash));
                }
            }
        }
        return lastValidationError is null
            ? Result<string>.Success(lastStatus)
            : Result<string>.Fail(lastValidationError, lastStatus);
    }

    private static NewPayloadWithWitnessV1Result GetWitnessResult(JsonRpcResponse response, int payloadVersion) =>
        response switch
        {
            ResultWrapper<NewPayloadWithWitnessV1Result> { Result.ResultType: ResultType.Success } resultWrapper => resultWrapper.Data,
            JsonRpcSuccessResponse { Result: NewPayloadWithWitnessV1Result result } => result,
            _ => throw new AssertionException($"engine_newPayloadWithWitness (V{payloadVersion}) returned unexpected response type {response.GetType().FullName}")
        };

    private static bool TryGetRpcError(JsonRpcResponse response, out int errorCode, out string? errorMessage)
    {
        switch (response)
        {
            case JsonRpcErrorResponse errorResponse:
                errorCode = errorResponse.Error?.Code ?? ErrorCodes.InternalError;
                errorMessage = errorResponse.Error?.Message;
                return true;
            case IResultWrapper { Result.ResultType: ResultType.Failure } resultWrapper:
                errorCode = resultWrapper.ErrorCode;
                errorMessage = resultWrapper.Result.Error;
                return true;
            default:
                errorCode = 0;
                errorMessage = null;
                return false;
        }
    }

    private static PayloadStatusV1 GetPayloadStatus(JsonRpcResponse response, int payloadVersion) =>
        response switch
        {
            ResultWrapper<PayloadStatusV1> { Result.ResultType: ResultType.Success } resultWrapper => resultWrapper.Data,
            JsonRpcSuccessResponse { Result: PayloadStatusV1 payloadStatus } => payloadStatus,
            _ => throw new AssertionException($"engine_newPayloadV{payloadVersion} returned unexpected response type {response.GetType().FullName}")
        };

    private static void AssertExpectedRpcError(int errorCode, string? errorMessage, string? validationError, int payloadVersion) =>
        Assert.That(validationError, Is.Not.Null, $"engine_newPayloadV{payloadVersion} RPC error: {errorCode} {errorMessage}");

    private static void AssertPayloadStatus(PayloadStatusV1 payloadStatus, string? expectedValidationError, int payloadVersion, string? explicitStatus = null)
    {
        // A fixture-supplied `status` wins (covers INCLUSION_LIST_UNSATISFIED for FOCIL);
        // otherwise fall back to the legacy validation-error → INVALID convention.
        string expectedStatus = explicitStatus ?? (expectedValidationError is null ? PayloadStatus.Valid : PayloadStatus.Invalid);
        Assert.That(payloadStatus.Status, Is.EqualTo(expectedStatus), $"engine_newPayloadV{payloadVersion} returned {payloadStatus.Status}, expected {expectedStatus}. ValidationError: {payloadStatus.ValidationError}");

        if (expectedValidationError is not null)
            AssertValidationError(payloadStatus.ValidationError, expectedValidationError, payloadVersion);
    }

    private static void AssertValidationError(string? actualError, string expectedError, int payloadVersion)
    {
        Assert.That(actualError, Is.Not.Null, $"engine_newPayloadV{payloadVersion} returned INVALID without validation error. Expected: {expectedError}");

        string[] mapped = MapValidationErrorsToEestExceptions(actualError!);
        string[] expected = expectedError.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool matches = expected.Any(e => mapped.Contains(e) || actualError!.Contains(e, StringComparison.Ordinal));

        // BAL-aware execution rejects malformed/negative blocks via BAL mismatch (the generated
        // BAL doesn't match the suggested one) before tx-level validation runs. EEST's
        // *_from_state_test synthesized fixtures and a few other negative tests ship incomplete
        // BALs because the original test was authored against tx-level rejection. Accept any
        // suggested-block-level-access-list mismatch as equivalent to the tx-level rejection
        // the fixture expected — the block IS invalid; only the failure mode differs.
        if (!matches && actualError is not null
            && actualError.Contains("Suggested block-level access list", StringComparison.Ordinal))
        {
            matches = true;
        }

        Assert.That(matches, Is.True, $"engine_newPayloadV{payloadVersion} unexpected validation error. Actual: {actualError}. Mapped: {string.Join("|", mapped)}. Expected: {expectedError}");
    }

    // Mirrors execution-specs NethermindExceptionMapper: client validation text -> EEST exception ids.
    private static readonly (string ExpectedError, string Substring)[] ValidationErrorSubstringMappings =
    [
        ("TransactionException.SENDER_NOT_EOA", "sender has deployed code"),
        ("TransactionException.INTRINSIC_GAS_TOO_LOW", "intrinsic gas too low"),
        ("TransactionException.INTRINSIC_GAS_BELOW_FLOOR_GAS_COST", "intrinsic gas too low"),
        ("TransactionException.INSUFFICIENT_MAX_FEE_PER_GAS", "miner premium is negative"),
        ("TransactionException.INSUFFICIENT_MAX_FEE_PER_GAS", "max fee per gas less than block base fee"),
        ("TransactionException.PRIORITY_GREATER_THAN_MAX_FEE_PER_GAS", "InvalidMaxPriorityFeePerGas: Cannot be higher than maxFeePerGas"),
        ("TransactionException.GAS_ALLOWANCE_EXCEEDED", "Block gas limit exceeded"),
        ("TransactionException.NONCE_IS_MAX", "NonceTooHigh"),
        ("TransactionException.INITCODE_SIZE_EXCEEDED", "max initcode size exceeded"),
        ("TransactionException.NONCE_MISMATCH_TOO_LOW", "nonce too low"),
        ("TransactionException.NONCE_MISMATCH_TOO_HIGH", "nonce too high"),
        ("TransactionException.INSUFFICIENT_MAX_FEE_PER_BLOB_GAS", "max fee per blob gas less than block blob gas fee"),
        ("TransactionException.TYPE_1_TX_PRE_FORK", "InvalidTxType: Transaction type in"),
        ("TransactionException.TYPE_2_TX_PRE_FORK", "InvalidTxType: Transaction type in"),
        ("TransactionException.TYPE_3_TX_PRE_FORK", "InvalidTxType: Transaction type in"),
        ("TransactionException.TYPE_3_TX_ZERO_BLOBS", "blob transaction must have at least 1 blob"),
        ("TransactionException.TYPE_3_TX_INVALID_BLOB_VERSIONED_HASH", "InvalidBlobVersionedHashVersion: Blob version not supported"),
        ("TransactionException.TYPE_3_TX_CONTRACT_CREATION", "blob transaction of type create"),
        ("TransactionException.TYPE_4_EMPTY_AUTHORIZATION_LIST", "EIP-7702 transaction with empty auth list"),
        ("TransactionException.TYPE_4_TX_CONTRACT_CREATION", "EIP-7702 transaction cannot be used to create contract"),
        ("TransactionException.TYPE_4_TX_PRE_FORK", "InvalidTxType: Transaction type in"),
        ("TransactionException.INVALID_SIGNATURE_VRS", "InvalidTxSignature: Signature is invalid"),
        ("TransactionException.INVALID_CHAINID", "InvalidTxChainId"),
        ("TransactionException.INVALID_CHAINID", "InvalidTxSignature: Signature is invalid"),
        // HeaderBlobGasMismatch covers both wrong header.BlobGasUsed and an
        // inflated header value when real tx blob gas stays below the limit.
        // Real tx overflow is handled by the BlockBlobGasExceeded regex below.
        ("BlockException.INCORRECT_BLOB_GAS_USED", "HeaderBlobGasMismatch:"),
        ("BlockException.BLOB_GAS_USED_ABOVE_LIMIT", "HeaderBlobGasMismatch:"),
        ("BlockException.INVALID_REQUESTS", "InvalidRequestsHash: Requests hash mismatch in block"),
        ("BlockException.INVALID_GAS_USED_ABOVE_LIMIT", "ExceededGasLimit:"),
        ("BlockException.GAS_USED_OVERFLOW", "ExceededGasLimit:"),
        ("BlockException.RLP_BLOCK_LIMIT_EXCEEDED", "ExceededBlockSizeLimit: Exceeded block size limit"),
        ("BlockException.INVALID_DEPOSIT_EVENT_LAYOUT", "DepositsInvalid: Invalid deposit event layout:"),
        ("BlockException.INVALID_BASEFEE_PER_GAS", "InvalidBaseFeePerGas: Does not match calculated"),
        ("BlockException.INVALID_BLOCK_TIMESTAMP_OLDER_THAN_PARENT", "InvalidTimestamp: Timestamp in header cannot be lower than ancestor"),
        ("BlockException.INVALID_BLOCK_NUMBER", "InvalidBlockNumber: Block number does not match the parent"),
        ("BlockException.EXTRA_DATA_TOO_BIG", "InvalidExtraData: Extra data in header is not valid"),
        ("BlockException.INVALID_GASLIMIT", "InvalidGasLimit: Gas limit is not correct"),
        ("BlockException.INVALID_RECEIPTS_ROOT", "InvalidReceiptsRoot: Receipts root in header does not match"),
        ("BlockException.INVALID_LOG_BLOOM", "InvalidLogsBloom: Logs bloom in header does not match"),
        ("BlockException.INVALID_STATE_ROOT", "InvalidStateRoot: State root in header does not match"),
        ("BlockException.GAS_USED_OVERFLOW", "Block gas limit exceeded"), // alternate error string
        ("BlockException.BLOCK_ACCESS_LIST_GAS_LIMIT_EXCEEDED", "BlockAccessListGasLimitExceeded:"),
        ("TransactionException.GAS_ALLOWANCE_EXCEEDED", "BlockAccessListGasLimitExceeded:"),
    ];

    private const RegexOptions ValidationErrorRegexOptions = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly (string ExpectedError, Regex Pattern)[] ValidationErrorRegexMappings =
    [
        ("TransactionException.INSUFFICIENT_ACCOUNT_FUNDS", ValidationErrorRegex(@"insufficient funds for gas \* price \+ value|insufficient funds for transfer|insufficient funds for gas|insufficient sender balance|insufficient MaxFeePerGas for sender balance")),
        ("TransactionException.TYPE_3_TX_WITH_FULL_BLOBS", ValidationErrorRegex(@"Transaction \d+ is not valid")),
        ("TransactionException.TYPE_3_TX_MAX_BLOB_GAS_ALLOWANCE_EXCEEDED", ValidationErrorRegex(@"BlockBlobGasExceeded: A block cannot have more than \d+ blob gas, blobs count \d+, blobs gas used: \d+")),
        ("TransactionException.TYPE_3_TX_BLOB_COUNT_EXCEEDED", ValidationErrorRegex(@"BlobTxGasLimitExceeded: Transaction's totalDataGas=\d+ exceeded MaxBlobGas per transaction=\d+")),
        ("TransactionException.GAS_LIMIT_EXCEEDS_MAXIMUM", ValidationErrorRegex(@"TxGasLimitCapExceeded:")),
        ("TransactionException.INTRINSIC_GAS_TOO_LOW", ValidationErrorRegex(@"TxGasLimitCapExceeded: Intrinsic gas")),
        ("BlockException.INCORRECT_EXCESS_BLOB_GAS", ValidationErrorRegex(@"HeaderExcessBlobGasMismatch: Excess blob gas in header does not match calculated|Overflow in excess blob gas")),
        ("BlockException.INVALID_BLOCK_HASH", ValidationErrorRegex(@"Invalid block hash 0x[0-9a-f]+ does not match calculated hash 0x[0-9a-f]+")),
        ("BlockException.INCORRECT_BLOCK_FORMAT", ValidationErrorRegex(@"Invalid block hash 0x[0-9a-f]+ does not match calculated hash 0x[0-9a-f]+")),
        ("BlockException.SYSTEM_CONTRACT_EMPTY", ValidationErrorRegex(@"(Withdrawals|Consolidations)Empty: Contract is not deployed\.")),
        ("BlockException.SYSTEM_CONTRACT_CALL_FAILED", ValidationErrorRegex(@"(Withdrawals|Consolidations)Failed: Contract execution failed\.")),
        ("BlockException.INVALID_BAL_HASH", ValidationErrorRegex(@"InvalidBlockLevelAccessListHash:")),
        ("BlockException.INVALID_BLOCK_ACCESS_LIST", ValidationErrorRegex(@"InvalidBlockLevelAccessListHash:|InvalidBlockLevelAccessList:|Error decoding block access list:")),
        ("BlockException.INCORRECT_BLOCK_FORMAT", ValidationErrorRegex(@"Error decoding block access list:")),
        ("TransactionException.GAS_ALLOWANCE_EXCEEDED", ValidationErrorRegex(@"TxGasLimitCapExceeded:")),
        ("BlockException.INVALID_BAL_EXTRA_ACCOUNT", ValidationErrorRegex(@"Error decoding block access list:.*Account changes were in incorrect order")),
        ("BlockException.INVALID_BAL_MISSING_ACCOUNT", ValidationErrorRegex(@"InvalidBlockLevelAccessList: Suggested block-level access list missing account changes")),
        ("BlockException.INVALID_DEPOSIT_EVENT_LAYOUT", ValidationErrorRegex(@"InvalidBlockLevelAccessList: Suggested block-level access list missing account changes")),
        // Nethermind currently reports these BAL system-contract failures with the same block access list validation message.
        ("BlockException.SYSTEM_CONTRACT_CALL_FAILED", ValidationErrorRegex(@"InvalidBlockLevelAccessList: Suggested block-level access list missing account changes")),
    ];

    private static string[] MapValidationErrorsToEestExceptions(string validationError) =>
    [
        .. ValidationErrorSubstringMappings
            .Where(m => validationError.Contains(m.Substring, StringComparison.Ordinal))
            .Select(m => m.ExpectedError)
            .Concat(ValidationErrorRegexMappings
                .Where(m => m.Pattern.IsMatch(validationError))
                .Select(m => m.ExpectedError))
            .Distinct()
    ];

    private static Regex ValidationErrorRegex(string pattern) => new(pattern, ValidationErrorRegexOptions);

    private static async Task<JsonRpcResponse> SendRpc(IJsonRpcService rpcService, JsonRpcContext context, string method, string paramsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(paramsJson);
        JsonRpcRequest request = new() { JsonRpc = "2.0", Id = 1, Method = method, Params = doc.RootElement.Clone() };
        return await rpcService.SendRequestAsync(request, context);
    }

    private static Task<JsonRpcResponse> SendFcu(IJsonRpcService rpcService, JsonRpcContext context, int fcuVersion, string blockHash) =>
        SendRpc(rpcService, context, "engine_forkchoiceUpdatedV" + fcuVersion, $$"""[{"headBlockHash":"{{blockHash}}","safeBlockHash":"{{blockHash}}","finalizedBlockHash":"{{blockHash}}"},null]""");

    private static void AssertRpcSuccess(JsonRpcResponse response)
    {
        Assert.That(response, Is.InstanceOf<IResultWrapper>(), response is JsonRpcErrorResponse err ? $"RPC error: {err.Error?.Code} {err.Error?.Message}" : "unexpected response type");
        Assert.That(((IResultWrapper)response).Result.ResultType, Is.EqualTo(ResultType.Success));
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

                // EEST omits blockHeader (and the parsed body fields) for invalid-block fixtures
                // because there is no canonical header for a block that must be rejected. The
                // hash/uncle assertions only make sense when the fixture provides a header, but
                // the block itself must still be enrolled so that validation actually runs and
                // ExpectException is honoured.
                if (testBlockJson.BlockHeader is not null)
                {
                    Assert.That(suggestedBlock.Header.Hash, Is.EqualTo(new Hash256(testBlockJson.BlockHeader.Hash)));

                    for (int uncleIndex = 0; uncleIndex < suggestedBlock.Uncles.Length; uncleIndex++)
                    {
                        Assert.That(suggestedBlock.Uncles[uncleIndex].Hash, Is.EqualTo(new Hash256(testBlockJson.UncleHeaders![uncleIndex].Hash)));
                    }
                }

                correctRlp.Add((suggestedBlock, testBlockJson.ExpectException));
            }
            catch (Exception e)
            {
                if (testBlockJson.ExpectException is null)
                {
                    string invalidRlpMessage = $"Invalid RLP ({i}) {e}";
                    Assert.That(!failOnInvalidRlp, invalidRlpMessage);
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

    /// <summary>Returns true when any test block carries an EELS-produced executionWitness.</summary>
    private static bool HasAnyExecutionWitness(BlockchainTest test)
    {
        if (test.Blocks is null) return false;
        foreach (TestBlockJson b in test.Blocks)
        {
            if (b.ExecutionWitness is not null) return true;
        }
        return false;
    }

    /// <summary>Regenerates each block's witness by re-executing it in the witness-generating sandbox and compares it to the fixture reference.</summary>
    private static void VerifyWitnesses(
        BlockchainTest test,
        IBlockTree blockTree,
        IWitnessGeneratingBlockProcessingEnvFactory factory,
        List<string> differences)
    {
        if (test.Blocks is null) return;
        foreach (TestBlockJson testBlockJson in test.Blocks)
        {
            ExecutionWitnessJson? expected = testBlockJson.ExecutionWitness;
            if (expected is null) continue;
            if (testBlockJson.ExecutionWitnessMutated == true) continue;
            if (testBlockJson.ExpectException is not null) continue;
            if (testBlockJson.BlockHeader is null) continue;

            Hash256 blockHash = new(testBlockJson.BlockHeader.Hash);
            Block? block = blockTree.FindBlock(blockHash);
            if (block is null)
            {
                differences.Add($"witness: block {blockHash} missing from tree");
                continue;
            }
            BlockHeader? parent = blockTree.FindHeader(block.ParentHash!);
            if (parent is null)
            {
                differences.Add($"witness: parent of {blockHash} missing from tree");
                continue;
            }

            // The collector re-processes the block directly, bypassing the pipeline's sender recovery, so a block
            // reloaded from the DB has RLP-decoded txs with no sender. Recover here, as BlockchainBridge does.
            if (block.Transactions.Length > 0)
            {
                EthereumEcdsa ecdsa = new(test.ChainId);
                foreach (Transaction tx in block.Transactions)
                    tx.SenderAddress ??= ecdsa.RecoverAddress(tx);
            }

            using IWitnessGeneratingBlockProcessingEnvScope scope = factory.CreateScope();
            IExistingBlockWitnessCollector collector = scope.Env.CreateExistingBlockWitnessCollector();
            using Witness actual = collector.GetWitnessForExistingBlock(parent, block);

            CompareWitnesses(blockHash, expected, actual, differences);
        }
    }

    private static void CompareWitnesses(
        Hash256 blockHash,
        ExecutionWitnessJson expected,
        Witness actual,
        List<string> differences)
    {
        ComputeDiff(blockHash, "state", expected.State, actual.State, differences);
        ComputeDiff(blockHash, "codes", expected.Codes, actual.Codes, differences);
        ComputeDiff(blockHash, "headers", expected.Headers, actual.Headers, differences);
    }

    private static void ComputeDiff(
        Hash256 blockHash,
        string section,
        string[]? expected,
        IOwnedReadOnlyList<byte[]> actual,
        List<string> differences)
    {
        int expectedCount = expected?.Length ?? 0;
        if (expectedCount != actual.Count)
            differences.Add($"witness {section} (block {blockHash}): count mismatch: expected {expectedCount}, produced {actual.Count}");

        int common = expectedCount < actual.Count ? expectedCount : actual.Count;
        for (int i = 0; i < common; i++)
        {
            byte[] e = Bytes.FromHexString(expected![i]);
            if (!e.AsSpan().SequenceEqual(actual[i]))
                differences.Add($"witness {section} (block {blockHash}) at index {i}: expected 0x{e.ToHexString()}, produced 0x{actual[i].ToHexString()}");
        }
        for (int i = common; i < expectedCount; i++)
            differences.Add($"witness {section} (block {blockHash}) at index {i}: expected 0x{Bytes.FromHexString(expected![i]).ToHexString()}, none produced");
        for (int i = common; i < actual.Count; i++)
            differences.Add($"witness {section} (block {blockHash}) at index {i}: produced 0x{actual[i].ToHexString()}, none expected");
    }
}
