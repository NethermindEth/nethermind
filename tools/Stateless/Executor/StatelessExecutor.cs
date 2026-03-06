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
    public static bool TryExecute(ReadOnlySpan<byte> data, out Block? processedBlock)
    {
        Witness witness;
        (Block suggestedBlock, witness, uint chainId) = InputSerializer.Deserialize(data);

        using (witness)
        {
            ISpecProvider specProvider = GetSpecProvider(chainId);
            IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);
            EthereumEcdsa ecdsa = new(chainId);

            foreach (Transaction tx in suggestedBlock.Transactions)
                tx.SenderAddress ??= ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

            return TryExecute(suggestedBlock, witness, specProvider, out processedBlock);
        }
    }

    public static bool TryExecute(
        Block suggestedBlock, Witness witness, ISpecProvider specProvider, out Block? processedBlock)
    {
        processedBlock = null;
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
            return false;

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

        if (!blockValidator.ValidateSuggestedBlock(suggestedBlock, parentHeader, out _))
            return false;

        StatelessBlockProcessingEnv blockProcessingEnv = new(
            witness, specProvider, Always.Valid, NullLogManager.Instance);

        using IDisposable scope = blockProcessingEnv.WorldState.BeginScope(parentHeader);

        IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

        (processedBlock, TxReceipt[] receipts) = blockProcessor.ProcessOne(
            suggestedBlock,
            ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance,
            specProvider.GetSpec(suggestedBlock.Header));

        return blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock);
    }

    private static ISpecProvider GetSpecProvider(uint chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}")
    };
}
