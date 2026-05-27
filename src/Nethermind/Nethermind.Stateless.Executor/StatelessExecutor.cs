// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stateless.Execution.IO;
using Nethermind.Trie;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        StatelessPayload payload = InputDecoder.Decode(data);
        ISpecProvider specProvider = GetSpecProvider(payload.ChainConfig);
        IReleaseSpec spec = specProvider.GetSpec(payload.Block.Header);
        EthereumEcdsa ecdsa = new(payload.ChainConfig.ChainId);

#if !ZK_EVM
        if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
#endif

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

        Block processedBlock;
        TxReceipt[] receipts;

        try
        {
            (processedBlock, receipts) = blockProcessor.ProcessOne(
                suggestedBlock,
                ProcessingOptions.ReadOnlyChain,
                NullBlockTracer.Instance,
                specProvider.GetSpec(suggestedBlock.Header));
        }
        catch (Exception ex) when (ex is InvalidBlockException or MissingTrieNodeException)
        {
            Debug.Fail(ex.Message);
            return false;
        }

        if (!blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error))
        {
            Debug.Fail(error);
            return false;
        }

        return true;
    }

    private static ISpecProvider GetSpecProvider(ChainConfig chainConfig)
    {
        if (!ChainSpecBasedSpecProvider.KnownProvidersByChainId.TryGetValue(chainConfig.ChainId, out IForkAwareSpecProvider? baseProvider))
            throw new ArgumentException($"Unknown chain id: {chainConfig.ChainId}", nameof(chainConfig));

        // If the active fork is empty, use the base provider directly
        if (chainConfig.ActiveFork.Fork == 0 &&
            chainConfig.ActiveFork.Activation.BlockNumber.Length == 0 &&
            chainConfig.ActiveFork.Activation.Timestamp.Length == 0)
        {
            return baseProvider;
        }

        return StatelessSpecProvider.Create(baseProvider, chainConfig.ActiveFork);
    }
}
