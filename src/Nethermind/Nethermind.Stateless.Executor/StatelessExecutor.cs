// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        StatelessPayload payload = InputDecoder.Decode(data);
        ISpecProvider rawSpecProvider = GetSpecProvider(payload.ChainConfig.ChainId);
        // Pin spec to ActiveFork.Activation so synthetic-timestamp test fixtures
        // (EEST blocks with header.timestamp ~= 0) keep using the intended spec
        // throughout block processing, not the pre-Frontier spec picked from the header.
        ForkActivation pinnedFork = payload.ChainConfig.ActiveFork.Activation.ToForkActivation();
        ISpecProvider specProvider = new SingleReleaseSpecProvider(
            rawSpecProvider.GetSpec(pinnedFork), rawSpecProvider.NetworkId, rawSpecProvider.ChainId);
        IReleaseSpec spec = specProvider.GetSpec(pinnedFork);
        EthereumEcdsa ecdsa = new(payload.ChainConfig.ChainId);

        // Recover sender addresses for transactions,
        // as RLP-deserialized blocks don't have them
        foreach (Transaction tx in payload.Block.Transactions)
            tx.SenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

        using Witness witness = payload.Witness.ToWitness();
        bool success = Execute(payload.Block, witness, specProvider);
        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.NewPayloadRequestRoot,
            IsSuccess = success,
            ChainConfig = payload.ChainConfig
        };

        return StatelessValidationResult.Encode(result);
    }

    public static bool Execute(Block suggestedBlock, Witness witness, ISpecProvider specProvider)
    {
        BlockHeader? parentHeader = null;
        using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();

        foreach (BlockHeader header in headers)
        {
            if (header.Hash == suggestedBlock.Header.ParentHash)
            {
                parentHeader = header;
                break;
            }
        }

        if (parentHeader is null)
        {
            Debug.Fail("Witness is missing the parent header");
            return false;
        }

        StatelessBlockTree blockTree = new(headers);
        HeaderValidator headerValidator = new(
            blockTree,
            Always.Valid,
            specProvider,
            NullLogManager.Instance
        );
        BlockValidator blockValidator = new(
            new TxValidator(specProvider.ChainId),
            headerValidator,
            new UnclesValidator(blockTree, headerValidator, NullLogManager.Instance),
            specProvider,
            NullLogManager.Instance
        );

        if (!blockValidator.ValidateSuggestedBlock(suggestedBlock, parentHeader, out string? error))
        {
            Debug.Fail(error);
            return false;
        }

        StatelessBlockProcessingEnv blockProcessingEnv = new(
            witness, specProvider, Always.Valid, NullLogManager.Instance);

        using IDisposable scope = blockProcessingEnv.WorldState.BeginScope(parentHeader);

        IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

        (Block processedBlock, TxReceipt[] receipts) = blockProcessor.ProcessOne(
            suggestedBlock,
            ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance,
            specProvider.GetSpec(suggestedBlock.Header));

        if (!blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error))
        {
            Debug.Fail(error);
            return false;
        }

        return true;
    }

    private static ISpecProvider GetSpecProvider(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}", nameof(chainId))
    };
}
