// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Stateless.Execution;

public class StatelessExecutor
{
    public static Block Execute(ReadOnlySpan<byte> data)
    {
        (Block block, Witness witness, int chainId) = InputSerializer.Deserialize(data);

        return Execute(block, witness, chainId);
    }

    public static Block Execute(Block suggestedBlock, Witness witness, int chainId)
    {
        BlockHeader? baseBlock = null;

        foreach (BlockHeader header in witness.DecodedHeaders)
        {
            if (header.Hash == suggestedBlock.Header.ParentHash)
                baseBlock = header; // no break?
        }

        if (baseBlock is null)
            throw new InvalidOperationException("Base block cannot be found");

        ISpecProvider specProvider = GetSpecProvider(chainId);

        StatelessBlockProcessingEnv blockProcessingEnv = new(
            witness, specProvider, Always.Valid, NullLogManager.Instance);

        using IDisposable scope = blockProcessingEnv.WorldState.BeginScope(baseBlock);

        IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

        // TODO: Remove once the sender recovery is implemented
        suggestedBlock.Transactions[0].SenderAddress = new("0xaa2fbe31e6d774d2e70b1375f3bc791ae487fd50");
        suggestedBlock.Transactions[1].SenderAddress = new("0xa4a59a31360b4ab10d28755f53697b60c796ee03");

        (Block processedBlock, TxReceipt[] _) = blockProcessor.ProcessOne(
            suggestedBlock,
            ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance,
            specProvider.GetSpec(suggestedBlock.Header));

        if (processedBlock.Hash != suggestedBlock.Hash)
            throw new InvalidOperationException("Block hash mismatch");

        return processedBlock;
    }

    private static ISpecProvider GetSpecProvider(int chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}")
    };
}
