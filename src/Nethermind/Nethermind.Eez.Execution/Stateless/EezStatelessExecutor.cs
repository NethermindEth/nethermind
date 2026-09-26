// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Eez.Execution.Stateless;

/// <summary>
/// Re-executes a window of consecutive EEZ L2 blocks, each over its own execution witness, and checks that the
/// window chains: every block builds on the previous block's hash and post-state root.
/// </summary>
public sealed class EezStatelessExecutor(ISpecProvider specProvider, ILogManager logManager)
{
    private static readonly IRlpDecoder<TxReceipt> ReceiptTrieDecoder = Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);

    private readonly EthereumEcdsa _ecdsa = new(specProvider.ChainId);
    private readonly ExecutionRequestsProcessorFactory _executionRequestsProcessorFactory = new(EezExecutionRequests.Options);
    private readonly EezTransactionProcessorFactory _transactionProcessorFactory = new();

    /// <param name="window">The blocks, oldest first.</param>
    /// <param name="lastBlockCheckpoints">Strictly increasing transaction indices of the last block to checkpoint.</param>
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

        TransactionCheckpointRecorder? recorder = checkpoints.Length > 0 ? new TransactionCheckpointRecorder(specProvider, checkpoints) : null;
        StatelessBlockProcessingEnv env = new(input.Witness, specProvider, Always.Valid, logManager)
        {
            TransactionProcessorFactory = _transactionProcessorFactory,
            TxValidator = CreateTxValidator(),
            BlockValidatorFactory = (txValidator, headerValidator, unclesValidator) =>
                new EezBlockValidator(txValidator, headerValidator, unclesValidator, specProvider, logManager),
            ExecutionRequestsProcessorFactory = _executionRequestsProcessorFactory,
            TransactionProcessedEventHandler = recorder,
        };
        if (recorder is not null)
        {
            recorder.WorldState = env.WorldState;
        }

        StatelessBlockProcessingResult result = env.Process(block);
        if (!result.IsValid)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected, $"Block {block.Number} is invalid: {result.Error}");
        }

        EezTransactionCheckpoint[] transactionCheckpoints = recorder is null
            ? []
            : CreateCheckpoints(result.ProcessedBlock!, result.Receipts, checkpoints, recorder.StateRoots);
        return new EezStatelessBlockResult(result.ProcessedBlock!, result.Parent!.StateRoot!, result.Receipts, transactionCheckpoints);
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

    private static void EnsureCheckpointsInRange(Block block, int[] checkpoints)
    {
        for (int i = 0; i < checkpoints.Length; i++)
        {
            if (checkpoints[i] < 0 || checkpoints[i] >= block.Transactions.Length || (i > 0 && checkpoints[i] <= checkpoints[i - 1]))
            {
                throw new EezStatelessException(EezStatelessFailure.InternalInvariant,
                    $"Checkpoints must be strictly increasing transaction indices of block {block.Number}.");
            }
        }
    }

    private void AssignSenders(Block block)
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
        txValidator.RegisterValidator(EezConstants.SystemTxType, EezTxType.CreateValidator(specProvider.ChainId));
        return txValidator;
    }

    private EezTransactionCheckpoint[] CreateCheckpoints(Block block, TxReceipt[] receipts, int[] indices, Hash256[] stateRoots)
    {
        EnsureCandidatesDefined(block.Header);
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        EezTransactionCheckpoint[] checkpoints = new EezTransactionCheckpoint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            checkpoints[i] = new EezTransactionCheckpoint(indices[i], stateRoots[i], CandidateBlockHash(block, receipts, indices[i], stateRoots[i], spec));
        }

        if (indices[^1] == block.Transactions.Length - 1 && stateRoots[^1] != block.Header.StateRoot)
        {
            throw new EezStatelessException(EezStatelessFailure.Rejected,
                $"Post-execution changes of block {block.Number} are not state neutral.");
        }

        return checkpoints;
    }

    /// <summary>
    /// A candidate block commits to the transaction prefix; every other field is copied from the executed block.
    /// </summary>
    private static Hash256 CandidateBlockHash(Block block, TxReceipt[] receipts, int lastIndex, Hash256 stateRoot, IReleaseSpec spec)
    {
        int count = lastIndex + 1;
        BlockHeader candidate = block.Header.Clone();
        candidate.StateRoot = stateRoot;
        candidate.TxRoot = TxTrie.CalculateRoot(block.Transactions.AsSpan(0, count));
        candidate.ReceiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts.AsSpan(0, count), ReceiptTrieDecoder);
        candidate.Bloom = new Bloom();
        for (int i = 0; i < count; i++)
        {
            candidate.Bloom.Accumulate(receipts[i].Bloom);
        }

        candidate.GasUsed = receipts[lastIndex].GasUsedTotal;
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
