// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Stateless.Execution;

public class StatelessExecutor
{
    public static bool TryExecute(ReadOnlySpan<byte> data, out Block? processedBlock)
    {
        (Block suggestedBlock, Witness witness, uint chainId) = InputSerializer.Deserialize(data);

        ISpecProvider specProvider = GetSpecProvider(chainId);
        //IReleaseSpec spec = specProvider.GetSpec(suggestedBlock.Header);
        //EthereumEcdsa ecdsa = new(chainId);

        //foreach (Transaction tx in suggestedBlock.Transactions)
        //    tx.SenderAddress ??= ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

        return TryExecute(suggestedBlock, witness, specProvider, out processedBlock);
    }

    public static bool TryExecute(
        Block suggestedBlock, Witness witness, ISpecProvider specProvider, out Block? processedBlock)
    {
        processedBlock = null;
        BlockHeader? baseBlock = null;

        foreach (BlockHeader header in witness.DecodedHeaders)
        {
            if (header.Hash == suggestedBlock.Header.ParentHash)
                baseBlock = header; // no break?
        }

        if (baseBlock is null)
            return false;

        StatelessBlockProcessingEnv blockProcessingEnv = new(
            witness, specProvider, Always.Valid, NullLogManager.Instance);

        using IDisposable scope = blockProcessingEnv.WorldState.BeginScope(baseBlock);

        IBlockProcessor blockProcessor = blockProcessingEnv.BlockProcessor;

        // TODO: Remove once the sender recovery is implemented
        suggestedBlock.Transactions[0].SenderAddress ??= new("0xaa2fbe31e6d774d2e70b1375f3bc791ae487fd50");
        suggestedBlock.Transactions[1].SenderAddress ??= new("0xa4a59a31360b4ab10d28755f53697b60c796ee03");

        (processedBlock, TxReceipt[] _) = blockProcessor.ProcessOne(
            suggestedBlock,
            ProcessingOptions.ReadOnlyChain,
            NullBlockTracer.Instance,
            specProvider.GetSpec(suggestedBlock.Header));

        if (processedBlock.Hash != suggestedBlock.Hash)
            return false;

        return true;
    }

    private static ISpecProvider GetSpecProvider(uint chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}")
    };
}
