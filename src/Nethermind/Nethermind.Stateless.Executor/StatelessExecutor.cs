// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
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
using Nethermind.Specs.Forks;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static Block Execute(ReadOnlySpan<byte> data)
    {
        Witness witness;
        (Block suggestedBlock, witness, ulong chainId, byte[]? chainConfigJson) =
            InputSerializer.DeserializeWithChainConfig(data);

        using (witness)
        {
            ISpecProvider specProvider = chainConfigJson is { Length: > 0 }
                ? BuildSpecProviderFromGenesis(chainConfigJson, suggestedBlock.Header.Timestamp)
                : GetSpecProvider(chainId);
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

    private static ISpecProvider BuildSpecProviderFromGenesis(byte[] chainConfigJson, ulong blockTimestamp)
    {
        using JsonDocument doc = JsonDocument.Parse(chainConfigJson);
        JsonElement config = doc.RootElement.GetProperty("config");
        ulong chainId = config.GetProperty("chainId").GetUInt64();
        IReleaseSpec spec = SelectLatestActivatedSpec(config, blockTimestamp);
        return new SingleReleaseSpecProvider(spec, networkId: chainId, chainId: chainId);
    }

    /// <remarks>
    /// Picks the latest timestamp-based fork whose activation time is &lt;= <paramref name="blockTimestamp"/>.
    /// Block-number-based forks (Berlin, London, …) are intentionally not handled here: the stateless
    /// executor is only used against post-Shanghai (Merge-era) chains, where every fork is time-gated.
    /// A config that does not activate any of the known timestamp forks at <paramref name="blockTimestamp"/>
    /// is rejected rather than silently downgraded to Frontier, which would otherwise mask
    /// missing-fork bugs at the application layer.
    /// </remarks>
    private static IReleaseSpec SelectLatestActivatedSpec(JsonElement config, ulong blockTimestamp)
    {
        if (HasActivatedTime(config, "osakaTime", blockTimestamp)) return Osaka.Instance;
        if (HasActivatedTime(config, "pragueTime", blockTimestamp)) return Prague.Instance;
        if (HasActivatedTime(config, "cancunTime", blockTimestamp)) return Cancun.Instance;
        if (HasActivatedTime(config, "shanghaiTime", blockTimestamp)) return Shanghai.Instance;

        throw new NotSupportedException(
            $"Embedded chain_config has no recognized timestamp-based fork activated at block " +
            $"timestamp {blockTimestamp}. The stateless executor supports post-Shanghai chains only " +
            "(shanghaiTime / cancunTime / pragueTime / osakaTime).");
    }

    private static bool HasActivatedTime(JsonElement config, string field, ulong blockTimestamp)
        => config.TryGetProperty(field, out JsonElement v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetUInt64(out ulong forkTime)
            && forkTime <= blockTimestamp;
}
