// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// Re-executes a window of consecutive EEZ L2 blocks under the chain's <see cref="EezChainRules"/>, each over its own
/// execution witness, and checks that the window chains: every block builds on the previous block's hash and
/// post-state root.
/// </summary>
public sealed partial class EezStatelessExecutor(ISpecProvider specProvider, EezChainRules rules, ILogManager logManager)
{
    private readonly EthereumEcdsa _ecdsa = new(specProvider.ChainId);
    private readonly EezTransactionProcessorFactory _transactionProcessorFactory = new(rules);

    /// <param name="window">The blocks, oldest first.</param>
    /// <param name="lastBlockCheckpoints">
    /// Strictly increasing transaction indices of the last block to checkpoint, led by
    /// <see cref="EezTransactionCheckpoint.PreExecution"/> for its empty prefix.
    /// </param>
    /// <exception cref="EezStatelessException">The window is invalid, or the checkpoints are impossible.</exception>
    public EezStatelessBlockResult[] Execute(IReadOnlyList<EezStatelessBlock> window, ReadOnlySpan<int> lastBlockCheckpoints)
    {
        if (window.Count == 0)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, "The window has no blocks.");
        }

        EezStatelessBlockResult[] results = new EezStatelessBlockResult[window.Count];
        for (int i = 0; i < window.Count; i++)
        {
            int[] checkpoints = i == window.Count - 1 ? lastBlockCheckpoints.ToArray() : [];
            results[i] = ExecuteBlock(window[i], checkpoints);
            if (i > 0)
            {
                EnsureChains(results[i - 1], results[i]);
            }
        }

        return results;
    }

    private EezStatelessBlockResult ExecuteBlock(EezStatelessBlock input, int[] checkpoints)
    {
        Block block = Decode(input.Rlp);
        EnsureCheckpointsInRange(block, checkpoints);
        AssignSenders(block);

        NativeHistoryContext? native = rules.UsesNativeContract ? _nativeHistory : null;
        NativeCheckpointProjection? projection = checkpoints.Length > 0 && rules.FinalizesProtocolBalances
            ? new(rules.ProtocolBalanceAccounts, logManager) : null;
        TransactionCheckpointRecorder? recorder = checkpoints.Length > 0
            ? new TransactionCheckpointRecorder(specProvider, checkpoints, native is null || checkpoints[0] != EezTransactionCheckpoint.PreExecution ? null : native.ValidateEmptyPrefix,
                projection is null ? null : projection.Project)
            : null;
        StatelessBlockProcessingEnv env = CreateEnvironment(input.Witness, _transactionProcessorFactory, recorder,
            recorder is not null ? recorder.Wrap : null, projection);
        StatelessBlockProcessingResult result = env.Process(block);
        if (!result.IsValid)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, $"Block {block.Number} is invalid: {result.Error}");
        }

        EezTransactionCheckpoint[] transactionCheckpoints = recorder is null
            ? []
            : CreateCheckpoints(result.ProcessedBlock!, result.Receipts, checkpoints, recorder.StateRoots);
        return new EezStatelessBlockResult(result.ProcessedBlock!, result.Parent!, result.Receipts, transactionCheckpoints);
    }

    /// <summary>An environment that executes blocks under the chain's EEZ rules over <paramref name="witness"/>.</summary>
    public StatelessBlockProcessingEnv CreateEnvironment(Witness witness,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessed = null,
        Func<IBlockProcessor.IBlockTransactionsExecutor, IWorldState, IBlockProcessor.IBlockTransactionsExecutor>? transactionsExecutorDecorator = null) =>
        CreateEnvironment(witness, _transactionProcessorFactory, transactionProcessed, transactionsExecutorDecorator);

    internal StatelessBlockProcessingEnv CreateEnvironment(Witness witness, ITransactionProcessorFactory processorFactory,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessed,
        Func<IBlockProcessor.IBlockTransactionsExecutor, IWorldState, IBlockProcessor.IBlockTransactionsExecutor>? transactionsExecutorDecorator,
        NativeCheckpointProjection? projection = null) =>
        new(witness, specProvider, Always.Valid, logManager)
        {
            TransactionProcessorFactory = processorFactory,
            WorldStateScopeProviderFactory = projection is null ? null : projection.CreateProvider,
            TxValidator = CreateTxValidator(),
            BlockValidatorFactory = (txValidator, headerValidator, unclesValidator) => new EezBlockValidator(txValidator,
                rules.IsRollupX ? new RollupXHeaderValidator(headerValidator, rules) : headerValidator, unclesValidator, specProvider, logManager, rules),
            ExecutionRequestsOptions = EezExecutionRequests.For(rules),
            TransactionProcessedEventHandler = transactionProcessed,
            TransactionsExecutorDecorator = (inner, worldState) => Decorate(inner, worldState, transactionsExecutorDecorator),
            BeaconBlockRootHandlerFactory = rules.CallsBeaconRootsContract ? null : static (_, _) => NoBeaconRootsCall.Instance,
        };

    private IBlockProcessor.IBlockTransactionsExecutor Decorate(IBlockProcessor.IBlockTransactionsExecutor inner, IWorldState worldState,
        Func<IBlockProcessor.IBlockTransactionsExecutor, IWorldState, IBlockProcessor.IBlockTransactionsExecutor>? decorator)
    {
        IBlockProcessor.IBlockTransactionsExecutor decorated = decorator?.Invoke(inner, worldState) ?? inner;
        decorated = _nativeHistory?.Wrap(decorated, worldState) ?? decorated;
        return rules.ProtocolBalanceAccounts.Length == 0 ? decorated : new ProtocolBalanceCleanup(decorated, worldState, specProvider, rules);
    }

    private static Block Decode(byte[] rlp)
    {
        Block? block;
        try
        {
            block = Rlp.Decode<Block>(rlp);
        }
        catch (RlpException e)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, $"The block RLP is invalid: {e.Message}");
        }

        return block ?? throw new EezStatelessException(EezStatelessFailure.Rejected, "The block RLP is empty.");
    }

    private void EnsureCheckpointsInRange(Block block, int[] checkpoints)
    {
        for (int i = 0; i < checkpoints.Length; i++)
        {
            bool preExecution = i == 0 && checkpoints[i] == EezTransactionCheckpoint.PreExecution;
            if (!preExecution && (checkpoints[i] < 0 || checkpoints[i] >= block.Transactions.Length || (i > 0 && checkpoints[i] <= checkpoints[i - 1])))
            {
                throw new EezStatelessException(EezStatelessFailure.InternalInvariant,
                    $"Checkpoints must be the empty prefix, if any, then strictly increasing transaction indices of block {block.Number}.");
            }
        }
        if (rules.UsesNativeContract && checkpoints.Length > 0)
        {
            NativeExecutionGroup group = default;
            int checkpoint = checkpoints[0] == EezTransactionCheckpoint.PreExecution ? 1 : 0;
            for (int index = 0; checkpoint < checkpoints.Length && index < block.Transactions.Length; index++)
            {
                group = group.After(block.Transactions[index], out _);
                if (checkpoints[checkpoint] == index)
                {
                    group.EnsureComplete();
                    checkpoint++;
                }
            }
        }
    }

    internal void AssignSenders(Block block)
    {
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.IsEezSystemTransaction())
            {
                continue;
            }

            if (!_ecdsa.TryRecoverAddress(tx, out Address? sender, !spec.ValidateChainId))
            {
                throw new EezStatelessException(EezStatelessFailure.Rejected, $"Transaction {tx.Hash} of block {block.Number} has no recoverable sender.");
            }

            tx.SenderAddress = sender;
        }
    }

    private TxValidator CreateTxValidator()
    {
        TxValidator txValidator = new(specProvider.ChainId);
        txValidator.RegisterValidator(rules.SystemTransactionType, EezTxType.CreateValidator(specProvider.ChainId, rules));
        if (rules.UsesNativeContract)
            txValidator.RegisterValidator((TxType)NativeHistoryEnvelope.Type, EezTxType.CreateValidator(specProvider.ChainId, rules));
        return txValidator;
    }

    private EezTransactionCheckpoint[] CreateCheckpoints(Block block, TxReceipt[] receipts, int[] indices, Hash256[] stateRoots)
    {
        EnsureCandidatesDefined(block.Header);
        using CandidatePrefix prefix = new(specProvider.GetSpec(block.Header), block.Transactions.Length);
        EezTransactionCheckpoint[] checkpoints = new EezTransactionCheckpoint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            prefix.Extend(block.Transactions, receipts, indices[i] + 1);
            checkpoints[i] = new EezTransactionCheckpoint(indices[i], stateRoots[i], CandidateBlockHash(block, receipts, prefix, stateRoots[i]));
        }

        if (indices[^1] == block.Transactions.Length - 1 && stateRoots[^1] != block.Header.StateRoot)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected,
                $"Post-execution changes of block {block.Number} are not state neutral.");
        }

        return checkpoints;
    }

    /// <summary>
    /// A candidate block commits to the transaction prefix; every other field is copied from the executed block. The
    /// empty prefix has empty transaction and receipt tries, an empty bloom and no gas used.
    /// </summary>
    private static Hash256 CandidateBlockHash(Block block, TxReceipt[] receipts, CandidatePrefix prefix, Hash256 stateRoot)
    {
        BlockHeader candidate = block.Header.Clone();
        candidate.StateRoot = stateRoot;
        candidate.TxRoot = prefix.TxRoot;
        candidate.ReceiptsRoot = prefix.ReceiptsRoot;
        candidate.Bloom = prefix.Bloom.Clone();
        candidate.GasUsed = prefix.Count == 0 ? 0 : receipts[prefix.Count - 1].GasUsedTotal;
        return candidate.CalculateHash();
    }

    private static void EnsureCandidatesDefined(BlockHeader header)
    {
        if (header.BlockAccessListHash is not null
            || (header.RequestsHash is not null && header.RequestsHash != ExecutionRequestExtensions.EmptyRequestsHash)
            || header.BlobGasUsed is > 0)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected,
                $"Block {header.Number} carries a block access list, requests or blobs, which a transaction prefix cannot commit to.");
        }
    }

    private static void EnsureChains(EezStatelessBlockResult previous, EezStatelessBlockResult next)
    {
        if (next.Block.ParentHash != previous.Hash || next.PreStateRoot != previous.PostStateRoot)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected,
                $"Block {next.Block.Number} does not build on block {previous.Block.Number}.");
        }
    }
}
