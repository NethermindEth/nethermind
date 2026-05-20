// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static Block Execute(ReadOnlySpan<byte> data)
    {
        Witness witness;
        (Block suggestedBlock, witness, ulong chainId) = InputSerializer.Deserialize(data);

        using (witness)
        {
            ISpecProvider specProvider = GetSpecProvider(chainId);
            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);
            EthereumEcdsa ecdsa = new(chainId);

            // Recover sender addresses for transactions,
            // as RLP-deserialized blocks don't have them
            foreach (Transaction tx in suggestedBlock.Transactions)
                tx.SenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

            return Execute(suggestedBlock, witness, specProvider);
        }
    }

    public static Block Execute(
        Block suggestedBlock, Witness witness, ISpecProvider specProvider)
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
            throw new InvalidOperationException("Witness is missing the parent header");

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
            throw new InvalidBlockException(suggestedBlock, error!);

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
            throw new InvalidBlockException(processedBlock, error!);

        return processedBlock;
    }

    private static ISpecProvider GetSpecProvider(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}", nameof(chainId))
    };
}
