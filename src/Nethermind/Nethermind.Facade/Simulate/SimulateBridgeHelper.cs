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
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    private void PrepareState(
        BlockStateCall<TransactionWithSourceDetails> blockStateCall,
        IWorldState stateProvider,
        IOverridableCodeInfoRepository codeInfoRepository,
        ulong blockNumber,
        IReleaseSpec releaseSpec)
    {
        releaseSpec = releaseSpec.WithoutEip158();
        stateProvider.ApplyStateOverridesNoCommit(codeInfoRepository, blockStateCall.StateOverrides, releaseSpec);

        TransactionWithSourceDetails[]? calls = blockStateCall.Calls;
        if (calls is not null)
        {
            if (calls.Length == 1)
            {
                Transaction transaction = calls[0].Transaction;
                Address? sender = transaction.SenderAddress;
                if (sender is not null)
                {
                    stateProvider.CreateAccountIfNotExists(sender, 0, 0);
                }

                Address? to = transaction.To;
                if (to is not null && !Equals(sender, to))
                {
                    stateProvider.CreateAccountIfNotExists(to, 0, 0);
                }
            }
            else
            {
                HashSet<Address> seenAddresses = new(calls.Length * 2, Address.EqualityComparer);
                for (int i = 0; i < calls.Length; i++)
                {
                    Transaction transaction = calls[i].Transaction;
                    CreateAccountIfNotExists(transaction.SenderAddress, stateProvider, seenAddresses);
                    CreateAccountIfNotExists(transaction.To, stateProvider, seenAddresses);
                }
            }
        }

        stateProvider.Commit(releaseSpec, commitRoots: true);
        stateProvider.CommitTree(blockNumber);
    }

    private static void CreateAccountIfNotExists(Address? address, IWorldState stateProvider, HashSet<Address> seenAddresses)
    {
        if (address is not null && seenAddresses.Add(address))
        {
            stateProvider.CreateAccountIfNotExists(address, 0, 0);
        }
    }

    public SimulateOutput<TTrace> TrySimulate<TTrace>(
        BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        ulong gasCapLimit,
        CancellationToken cancellationToken)
    {
        int blockCount = payload.BlockStateCalls?.Count ?? 0;
        List<SimulateBlockResult<TTrace>> list = new(blockCount);
        SimulateOutput<TTrace> result = new()
        {
            Items = list
        };

        try
        {
            Simulate(parent, payload, tracer, env, list, gasCapLimit, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            result.Error = ex.Message;
            result.IsInvalidInput = true;
        }
        catch (InvalidTransactionException ex)
        {
            result.Error = ex.Reason.ErrorDescription;
            result.TransactionResult = ex.Reason;
        }
        catch (InsufficientBalanceException ex)
        {
            result.Error = ex.Message;
            result.TransactionResult = TransactionResult.InsufficientSenderBalance;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private void Simulate<TTrace>(BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer,
        SimulateReadOnlyBlocksProcessingScope env,
        List<SimulateBlockResult<TTrace>> output,
        ulong gasCapLimit,
        CancellationToken cancellationToken)
    {
        IBlockTree blockTree = env.BlockTree;
        IWorldState stateProvider = env.WorldState;
        parent = GetParent(parent, payload, blockTree);

        env.SimulateRequestState.TotalGasLeft = gasCapLimit;

        if (payload.BlockStateCalls is not null)
        {
            Dictionary<Address, ulong> nonceCache = [];
            IBlockTracer cancellationBlockTracer = tracer.WithCancellation(cancellationToken);

            foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls)
            {
                nonceCache.Clear();

                (BlockHeader callHeader, IReleaseSpec spec) = GetCallHeader(env.SpecProvider, blockCall, parent, payload.Validation);
                env.SimulateRequestState.BlockGasLeft = callHeader.GasLimit;
                env.SimulateRequestState.BlockStateGasLeft = callHeader.GasLimit;
                callHeader.Hash = callHeader.CalculateHash();

                TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];

                env.SimulateRequestState.SetTxsWithExplicitGas(calls);

                PrepareState(blockCall, env.WorldState, env.CodeInfoRepository, callHeader.Number, spec);

                BlockBody body = AssembleBody(calls, stateProvider, nonceCache, spec);
                Block callBlock = new(callHeader, body);

                ProcessingOptions processingFlags = payload.Validation
                    ? SimulateProcessingOptions
                    : SimulateProcessingOptions | ProcessingOptions.NoValidation;

                env.SimulateRequestState.Validate = payload.Validation;
                env.SimulateRequestState.BlobBaseFeeOverride = spec.IsEip4844Enabled ? blockCall.BlockOverrides?.BlobBaseFee : null;

                (Block processedBlock, TxReceipt[] receipts) = env.BlockProcessor.ProcessOne(
                    callBlock,
                    processingFlags,
                    cancellationBlockTracer,
                    spec,
                    cancellationToken);

                stateProvider.CommitTree(processedBlock.Number);
                blockTree.SuggestBlock(processedBlock, BlockTreeSuggestOptions.ForceSetAsMain);
                blockTree.UpdateHeadBlock(processedBlock.Hash!);

                if (tracer is SimulateBlockTracer simulateTracer)
                {
                    simulateTracer.ReapplyBlockHash(processedBlock.Hash);
                }

                SimulateBlockResult<TTrace> blockResult = new(processedBlock, payload.ReturnFullTransactionObjects, specProvider)
                {
                    Calls = [.. tracer.BuildResult()],
                };

                output.Add(blockResult);
                parent = processedBlock.Header;
            }
        }
    }

    private BlockBody AssembleBody(
        TransactionWithSourceDetails[] calls,
        IWorldState stateProvider,
        Dictionary<Address, ulong> nonceCache,
        IReleaseSpec spec)
    {
        Transaction[] transactions = new Transaction[calls.Length];
        for (int i = 0; i < calls.Length; i++)
        {
            transactions[i] = CreateTransaction(calls[i], stateProvider, nonceCache);
        }

        Withdrawal[]? withdrawals = null;
        if (spec.WithdrawalsEnabled)
        {
            withdrawals = [];
        }

        BlockBody body = new(transactions, null, withdrawals);
        return body;
    }

    private static BlockHeader GetParent(BlockHeader parent, SimulatePayload<TransactionWithSourceDetails> payload, IBlockTree blockTree)
    {
        Block? latestBlock = blockTree.FindLatestBlock();
        ulong latestBlockNumber = latestBlock?.Number ?? 0;

        if (latestBlockNumber < parent.Number)
        {
            parent = latestBlock?.Header ?? blockTree.Head!.Header;
        }

        BlockStateCall<TransactionWithSourceDetails>? firstBlock =
            payload.BlockStateCalls is { Count: > 0 } blockStateCalls
                ? blockStateCalls[0]
                : null;

        ulong lastKnown = latestBlockNumber;
        if (firstBlock?.BlockOverrides?.Number > 0 && firstBlock.BlockOverrides?.Number < lastKnown)
        {
            Block? searchResult = blockTree.FindBlock(firstBlock.BlockOverrides.Number.Value - 1);
            if (searchResult is not null)
            {
                parent = searchResult.Header;
            }
        }

        return parent;
    }

    private Transaction CreateTransaction(
        TransactionWithSourceDetails transactionDetails,
        IWorldState stateProvider,
        Dictionary<Address, ulong> nonceCache)
    {
        Transaction? transaction = transactionDetails.Transaction;
        transaction.SenderAddress ??= Address.Zero;

        if (!transactionDetails.HadNonceInRequest)
        {
            ref ulong cachedNonce = ref CollectionsMarshal.GetValueRefOrAddDefault(nonceCache, transaction.SenderAddress, out bool exist);
            if (!exist)
            {
                if (stateProvider.TryGetAccount(transaction.SenderAddress, out AccountStruct test))
                {
                    cachedNonce = test.Nonce;
                }
                // else // Todo think if we shall create account here
            }
            else
            {
                cachedNonce++;
            }

            transaction.Nonce = cachedNonce;
        }

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

        if ((ForkActivation)result.Number >= specProvider.MergeBlockNumber)
        {
            result.IsPostMerge = true;
        }
        else
        {
            result.Difficulty = parent.Difficulty;
            result.IsPostMerge = false;
        }

        IReleaseSpec spec = specProvider.GetSpec(GetSimulatedActivation(block.BlockOverrides, result));

        if (spec.WithdrawalsEnabled) result.WithdrawalsRoot = Keccak.EmptyTreeHash;
        if (spec.IsBeaconBlockRootAvailable) result.ParentBeaconBlockRoot = Hash256.Zero;
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

    private static ForkActivation GetSimulatedActivation(BlockOverride? overrides, BlockHeader header) =>
        new(overrides?.Number ?? (ulong)header.Number, overrides?.Time ?? header.Timestamp);
}
