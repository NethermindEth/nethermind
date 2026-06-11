// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Proof;

/// <summary>
/// Implementation behind <c>proof_call</c>: executes a call at a block's post-state, collects the
/// execution witness, and returns it alongside the call result as <see cref="CallResultWithProof"/>.
/// </summary>
internal sealed class WitnessCall(
    IBlockFinder blockFinder,
    IBlockchainBridge blockchainBridge,
    ISpecProvider specProvider,
    IJsonRpcConfig jsonRpcConfig)
{
    /// <summary>
    /// Executes the call at the post-state of the requested block, collects the execution witness,
    /// and projects to <see cref="CallResultWithProof"/>. Pre-VM input errors fail the RPC (mirroring
    /// <c>eth_call</c>); in-VM failures (revert, out-of-gas, ...) are surfaced in the payload alongside
    /// the witness so a verifier can re-prove them.
    /// </summary>
    public ResultWrapper<CallResultWithProof> Execute(TransactionForRpc callRequest, BlockParameter? blockParameter)
    {
        blockParameter ??= BlockParameter.Latest;

        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<CallResultWithProof>.Fail(searchResult);
        }

        BlockHeader sourceHeader = searchResult.Object!;

        // Genesis has no parent header to walk back from; the witness machinery requires that.
        if (sourceHeader.Number == 0)
        {
            return ResultWrapper<CallResultWithProof>.Fail("Cannot generate witness for genesis block", ErrorCodes.InvalidInput);
        }

        // Default gas to the block gas limit so callers can omit it (mirrors eth_call).
        callRequest.Gas ??= sourceHeader.GasLimit;

        // Pass spec so that on pre-Berlin chains type-defaulted transactions are downgraded to Legacy
        // — matches eth_call / trace_call / debug_traceCall.
        Result<Transaction> txResult = callRequest.ToTransaction(validateUserInput: true, gasCap: jsonRpcConfig.GasCap, spec: specProvider.GetSpec(sourceHeader));
        if (!txResult.Success(out Transaction? tx, out string? error))
        {
            return ResultWrapper<CallResultWithProof>.Fail(error, ErrorCodes.InvalidInput);
        }

        tx.SenderAddress ??= Address.Zero;

        // Mirrors BlockchainBridge.CallAndRestore: derive the per-call header from the source
        // header so the EVM sees a consistent block context (IsPostMerge, base fee, blob fees).
        BlockHeader callHeader = sourceHeader.Clone();
        callHeader.GasUsed = 0;
        callHeader.MixHash = sourceHeader.MixHash;
        callHeader.IsPostMerge = sourceHeader.Difficulty == 0;

        IReleaseSpec releaseSpec = specProvider.GetSpec(callHeader);
        if (!callRequest.ShouldSetBaseFee())
        {
            callHeader.BaseFeePerGas = 0;
        }

        if (releaseSpec.IsEip4844Enabled)
        {
            callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(tx);
            callHeader.ExcessBlobGas = sourceHeader.ExcessBlobGas;

            BlobGasCalculator.TryCalculateFeePerBlobGas(callHeader, releaseSpec.BlobBaseFeeUpdateFraction, out UInt256 blobBaseFee);
            if (tx.Type is TxType.Blob && tx.MaxFeePerBlobGas is null)
            {
                tx.MaxFeePerBlobGas = blobBaseFee;
            }
        }

        tx.Hash = tx.CalculateHash();

        // Bound the EVM run by the RPC timeout so a heavy call can't pin a thread past the deadline; the
        // EVM then throws OperationCanceledException, which JsonRpcService maps to a Timeout error. A
        // MissingTrieNodeException from a reorg/prune between header lookup and witness execution is
        // likewise mapped (to ResourceNotFound) by JsonRpcService, so no local catch is needed here.
        using CancellationTokenSource timeout = jsonRpcConfig.BuildTimeoutCancellationToken();
        SingleCallWitnessResult result = blockchainBridge.GenerateExecutionWitness(callHeader, tx, timeout.Token);
        return CallResultWithProof.FromWitnessResult(result, tx.GasLimit);
    }
}
