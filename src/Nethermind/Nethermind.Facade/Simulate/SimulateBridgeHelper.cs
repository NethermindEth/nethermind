// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.Facade.Simulate;

public class SimulateBridgeHelper(IBlocksConfig blocksConfig, ISpecProvider specProvider)
{
    private const ProcessingOptions SimulateProcessingOptions =
        ProcessingOptions.ForceProcessing
        | ProcessingOptions.IgnoreParentNotOnMainChain
        | ProcessingOptions.StoreReceipts;

    /// <remarks>
    /// Only the state overrides are written, with EIP-158 off so an override that leaves an account empty keeps it.
    /// Senders and call targets are not created up front: like eth_call, an account the calls have not created yet must
    /// still be missing for the EIP-7702 authority check and EIP-8037 NEW_ACCOUNT charging, and the transaction
    /// processor creates a missing sender itself.
    /// </remarks>
    private static void PrepareState(
        BlockStateCall<TransactionWithSourceDetails> blockStateCall,
        IWorldState stateProvider,
        IOverridableCodeInfoRepository codeInfoRepository,
        ulong blockNumber,
        IReleaseSpec releaseSpec)
    {
        releaseSpec = releaseSpec.WithoutEip158();
        stateProvider.ApplyStateOverridesNoCommit(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec);
        stateProvider.Commit(releaseSpec, commitRoots: true);
        stateProvider.CommitTree(blockNumber);
    }

    /// <summary>The state and processor used to execute a simulated chain.</summary>
    public readonly record struct ExecutionEnvironment(IWorldState WorldState, ISpecProvider SpecProvider,
        IOverridableCodeInfoRepository CodeInfoRepository, SimulateRequestState Request, IBlockProcessor BlockProcessor);

    /// <summary>Processes blocks over the base state, returning any simulation error with the completed results.</summary>
    /// <param name="parent">The simulation base header.</param>
    /// <param name="payload">The prepared simulation input.</param>
    /// <param name="tracer">The tracer to use across all simulated blocks.</param>
    /// <param name="env">The already open simulation environment.</param>
    /// <param name="gasCapLimit">The total gas budget for this simulation.</param>
    /// <param name="cancellationToken">Cancels block and transaction processing.</param>
    /// <param name="blockTree">The full node's temporary block tree, if used for block lookup.</param>
    /// <param name="blockFinalized">Publishes the finalized simulated block to callers with a separate hash index.</param>
    /// <param name="propagateException">Identifies errors that require the caller to abandon or retry the attempt.</param>
    public SimulateOutput<TTrace> TrySimulate<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        ExecutionEnvironment env,
        ulong gasCapLimit,
        CancellationToken cancellationToken,
        IBlockTree? blockTree = null,
        Action<Block>? blockFinalized = null,
        Predicate<Exception>? propagateException = null)
    {
        int blockCount = payload.BlockStateCalls?.Count ?? 0;
        List<SimulateBlockResult<TTrace>> list = [with(blockCount)];
        SimulateOutput<TTrace> result = new()
        {
            Items = list
        };

        try
        {
            if (blockTree is not null) parent = GetParent(parent, blockTree);
            Simulate(parent, payload, tracer, env, list, gasCapLimit, cancellationToken, blockTree, blockFinalized);
        }
        catch (Exception exception) when (propagateException?.Invoke(exception) != true)
        {
            result.Error = exception.Message;
            switch (exception)
            {
                case ArgumentException:
                    result.IsInvalidInput = true;
                    break;
                case InvalidTransactionException invalidTransaction:
                    result.Error = invalidTransaction.Reason.ErrorDescription;
                    result.TransactionResult = invalidTransaction.Reason;
                    break;
                case InsufficientBalanceException:
                    result.TransactionResult = TransactionResult.InsufficientSenderBalance;
                    break;
            }
        }

        return result;
    }

    private void Simulate<TTrace>(BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        ExecutionEnvironment env,
        List<SimulateBlockResult<TTrace>> output,
        ulong gasCapLimit,
        CancellationToken cancellationToken,
        IBlockTree? blockTree,
        Action<Block>? blockFinalized)
    {
        SimulateRequestState request = env.Request;
        request.TotalGasLeft = gasCapLimit;
        IBlockTracer cancellationBlockTracer = tracer.WithCancellation(cancellationToken);
        ProcessingOptions processingFlags = payload.Validation
            ? SimulateProcessingOptions
            : SimulateProcessingOptions | ProcessingOptions.NoValidation;
        foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls ?? [])
        {
            (BlockHeader callHeader, IReleaseSpec spec) = GetCallHeader(env.SpecProvider, blockCall, parent, payload.Validation);
            request.BlockGasLeft = callHeader.GasLimit;
            request.BlockStateGasLeft = callHeader.GasLimit;
            callHeader.Hash = callHeader.CalculateHash();

            TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];
            request.Calls = calls;
            PrepareState(blockCall, env.WorldState, env.CodeInfoRepository, callHeader.Number, spec);
            Block callBlock = new(callHeader, AssembleBody(calls, blockCall.BlockOverrides?.Withdrawals, spec));

            request.Validate = payload.Validation;
            request.BlobBaseFeeOverride = spec.IsEip4844Enabled ? blockCall.BlockOverrides?.BlobBaseFee : null;

            (Block processedBlock, _) = env.BlockProcessor.ProcessOne(callBlock, processingFlags, cancellationBlockTracer, spec, cancellationToken);
            Hash256 blockHash = FinalizeBlock(processedBlock, spec, env.WorldState, tracer);
            if (blockTree is not null)
            {
                blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceSetAsMain);
                blockTree.UpdateHeadBlock(blockHash);
            }
            blockFinalized?.Invoke(processedBlock);

            output.Add(new SimulateBlockResult<TTrace>(processedBlock, payload.ReturnFullTransactionObjects, specProvider)
            {
                Calls = [.. tracer.BuildResult()],
            });
            parent = processedBlock.Header;
        }
    }

    /// <summary>
    /// Commits the simulated state and returns the block hash shared by the trace and the next block's parent link.
    /// </summary>
    /// <remarks>
    /// The default header encoding drops a zero base fee when no later field follows. Force the EIP-1559 encoding
    /// before publishing the simulated block's hash.
    /// </remarks>
    private static Hash256 FinalizeBlock(Block block, IReleaseSpec spec, IWorldState state, IBlockTracer tracer)
    {
        block.ExecutionRequests = null;
        if (spec.IsEip1559Enabled && block.Header.BaseFeePerGas.IsZero)
        {
            block.Header.Hash = block.Header.CalculateHash(RlpBehaviors.Eip1559Header);
            block.EncodedSize = (Rlp.GetDecoder<Block>() ?? new BlockDecoder()).GetLength(block, RlpBehaviors.Eip1559Header);
        }

        state.CommitTree(block.Number);
        Hash256 hash = block.Hash!;
        if (tracer is SimulateBlockTracer simulateTracer) simulateTracer.ReapplyBlockHash(hash);
        return hash;
    }

    private static BlockBody AssembleBody(
        TransactionWithSourceDetails[] calls,
        Withdrawal[]? withdrawalOverrides,
        IReleaseSpec spec)
    {
        Transaction[] transactions = new Transaction[calls.Length];
        for (int i = 0; i < calls.Length; i++)
        {
            transactions[i] = CreateTransaction(calls[i]);
        }

        Withdrawal[]? withdrawals = spec.WithdrawalsEnabled ? withdrawalOverrides ?? [] : null;

        BlockBody body = new(transactions, null, withdrawals);
        return body;
    }

    private static BlockHeader GetParent(BlockHeader parent, IBlockTree blockTree)
    {
        Block? latestBlock = blockTree.FindLatestBlock();
        ulong latestBlockNumber = latestBlock?.Number ?? 0;

        if (latestBlockNumber < parent.Number)
        {
            parent = latestBlock?.Header ?? blockTree.Head!.Header;
        }

        return parent;
    }

    private static Transaction CreateTransaction(TransactionWithSourceDetails transactionDetails)
    {
        Transaction? transaction = transactionDetails.Transaction;
        transaction.SenderAddress ??= Address.Zero;

        if (transaction.SupportsBlobs && transaction.BlobVersionedHashes is null) transaction.BlobVersionedHashes = [];
        if (transaction.AccessList is not null && transaction.AccessList.IsEmpty) transaction.AccessList = null;
        transaction.Hash ??= transaction.CalculateHash();

        return transaction;
    }

    internal (BlockHeader, IReleaseSpec) GetCallHeader(
        ISpecProvider specProvider,
        BlockStateCall<TransactionWithSourceDetails> block,
        BlockHeader parent,
        bool validate)
    {
        BlockHeader result = parent.CreateSimulatedChild(parent.Timestamp + blocksConfig.SecondsPerSlot);

        if (IsChildPostMerge(specProvider, parent, result.Number))
        {
            result.IsPostMerge = true;
        }
        else
        {
            result.Difficulty = parent.Difficulty;
            result.IsPostMerge = false;
        }

        IReleaseSpec spec = specProvider.GetSpec(GetSimulatedActivation(block.BlockOverrides, result));

        if (spec.WithdrawalsEnabled) result.WithdrawalsRoot = GetWithdrawalsRoot(block.BlockOverrides?.Withdrawals);
        if (spec.IsBeaconBlockRootAvailable) result.ParentBeaconBlockRoot = block.BlockOverrides?.BeaconRoot ?? Hash256.Zero;
        result.SlotNumber = GetSlotNumber(spec, specProvider, parent, result.SlotNumber, block.BlockOverrides?.Time ?? result.Timestamp);

        // In non-validation mode base fee is set to 0 if it is not overridden.
        // This is because it creates an edge case in EVM where gasPrice < baseFee.
        // Base fee could have been overridden.
        if (validate)
        {
            result.BaseFeePerGas = spec.BaseFeeCalculator.Calculate(parent, spec);
        }
        else
        {
            result.BaseFeePerGas = 0;
        }

        result.ExcessBlobGas = spec.IsEip4844Enabled ? BlobGasCalculator.CalculateExcessBlobGas(parent, spec) : null;

        block.BlockOverrides?.ApplyOverrides(result);

        return (result, spec);
    }

    /// <summary>
    /// Whether a simulated child of <paramref name="parent"/> is post-merge, which decides the consensus rules it runs
    /// and whether PREVRANDAO reads its mix hash (<c>blockOverrides.prevRandao</c>) or its difficulty.
    /// </summary>
    /// <remarks>
    /// The merge block number alone is not enough: a node that synced past the merge never records the terminal block,
    /// so it stays unknown on chains whose chainspec merges by total difficulty without pinning it (Mainnet, Sepolia,
    /// Gnosis, Chiado). Per EIP-3675 the child is post-merge when its parent is post-merge (zero difficulty) or when
    /// the parent reached the terminal total difficulty, which covers a child of the terminal PoW block itself.
    /// This mirrors the transition rule in <c>PoSSwitcher.GetBlockConsensusInfo</c>, which Facade cannot reference;
    /// keep the two aligned. An unknown (null) total difficulty falls back to the earlier rules, and a stored zero
    /// cannot reach a nonzero terminal total difficulty, so neither case needs a guard here.
    /// </remarks>
    private static bool IsChildPostMerge(ISpecProvider specProvider, BlockHeader parent, ulong childNumber) =>
        parent.Difficulty.IsZero
        || (ForkActivation)childNumber >= specProvider.MergeBlockNumber
        || (specProvider.TerminalTotalDifficulty is { } terminalTotalDifficulty && parent.TotalDifficulty >= terminalTotalDifficulty);

    /// <summary>
    /// Returns the EIP-7843 slot number of a simulated block, or <c>null</c> when its spec predates EIP-7843.
    /// </summary>
    /// <param name="spec">The spec resolved for the simulated block.</param>
    /// <param name="specProvider">Supplies the beacon chain genesis timestamp, if known.</param>
    /// <param name="parent">The block the simulated block builds on.</param>
    /// <param name="childSlot">The slot <see cref="BlockHeader.CreateSimulatedChild"/> assigned: the parent's slot + 1, if any.</param>
    /// <param name="timestamp">The simulated block's timestamp after <c>blockOverrides.time</c>.</param>
    /// <remarks>
    /// Pre-fork the result is <c>null</c> whatever <paramref name="childSlot"/> is, so the header is never encoded with a slot.
    /// Post-fork it is the beacon chain slot containing <paramref name="timestamp"/> where that can be derived, else
    /// <paramref name="childSlot"/>, else a synthetic 0 so SLOTNUM stays executable.
    /// <para>
    /// <c>slot = (timestamp - beacon genesis) / seconds per slot</c>, exact because every post-merge block sits on that grid.
    /// A slotless parent (normally the real base block) off the grid means the configured slot length or genesis is not this
    /// chain's, so derivation falls back. A parent carrying a slot may be a simulated block whose time override fell between
    /// slots and was advanced, so its grid slot is only a floor. The result stays above the parent's slot (its grid slot
    /// when it has none), as slots never repeat; that also caps a slot length too long for the chain at parent + 1.
    /// A slotless simulated parent timed between slots, which a pre-fork block can be, also falls back: it cannot be told
    /// apart from a base block on the wrong grid. The grid check is a heuristic: a wrong slot length still passes for a
    /// slotless parent on both grids (one Gnosis head in 12 with a 12 s slot length), and the slot is then wrong.
    /// </para>
    /// </remarks>
    private ulong? GetSlotNumber(IReleaseSpec spec, ISpecProvider specProvider, BlockHeader parent, ulong? childSlot, ulong timestamp)
    {
        if (!spec.IsEip7843Enabled)
        {
            return null;
        }

        ulong fallbackSlot = childSlot ?? 0;
        ulong secondsPerSlot = blocksConfig.SecondsPerSlot;
        if (specProvider.BeaconChainGenesisTimestamp is not { } genesis
            || secondsPerSlot == 0
            || parent.Timestamp < genesis
            || timestamp < genesis)
        {
            return fallbackSlot;
        }

        ulong parentOffset = parent.Timestamp - genesis;
        ulong parentGridSlot = parentOffset / secondsPerSlot;
        bool parentFitsGrid = parent.SlotNumber is { } parentSlot
            ? parentSlot >= parentGridSlot
            : parentOffset % secondsPerSlot == 0;
        if (!parentFitsGrid)
        {
            return fallbackSlot;
        }

        ulong previousSlot = parent.SlotNumber ?? parentGridSlot;
        ulong slot = (timestamp - genesis) / secondsPerSlot;
        return slot <= previousSlot ? previousSlot + 1 : slot;
    }

    private static Hash256 GetWithdrawalsRoot(Withdrawal[]? withdrawals) =>
        withdrawals is null || withdrawals.Length == 0 ? Keccak.EmptyTreeHash : new WithdrawalTrie(withdrawals).RootHash;

    private static ForkActivation GetSimulatedActivation(BlockOverride? overrides, BlockHeader header) =>
        new(overrides?.Number ?? (ulong)header.Number, overrides?.Time ?? header.Timestamp);
}
