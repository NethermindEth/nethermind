// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
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
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        StatelessPayload payload = InputDecoder.Decode(data);
        ISpecProvider specProvider = payload.ChainConfig.ChainConfigJson is { Length: > 0 }
            ? BuildSpecProviderFromGenesis(payload.ChainConfig.ChainConfigJson, payload.Block.Header.Timestamp)
            : GetSpecProvider(payload.ChainConfig.ChainId);
        IReleaseSpec spec = specProvider.GetSpec(payload.Block.Header);
        EthereumEcdsa ecdsa = new(payload.ChainConfig.ChainId);

        // Recover sender addresses for transactions,
        // as RLP-deserialized blocks don't have them
        foreach (Transaction tx in payload.Block.Transactions)
            tx.SenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

        using Witness witness = payload.Witness.ToWitness();
        bool success = Execute(payload.Block, witness, specProvider);
        // Drop the input envelope from the result — the JSON is large enough to
        // bust ziskos' output buffer, and consumers reconstruct chain identity
        // from ChainId + ActiveFork.
        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.NewPayloadRequestRoot,
            IsSuccess = success,
            ChainConfig = new()
            {
                ChainId = payload.ChainConfig.ChainId,
                ActiveFork = payload.ChainConfig.ActiveFork,
                ChainConfigJson = []
            }
        };

        byte[] enc = StatelessValidationResult.Encode(result);
        return enc;
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

    private static ISpecProvider BuildSpecProviderFromGenesis(byte[] chainConfigJson, ulong blockTimestamp)
    {
        using JsonDocument doc = JsonDocument.Parse(chainConfigJson);
        JsonElement config = doc.RootElement.GetProperty("config");
        ulong chainId = config.GetProperty("chainId").GetUInt64();
        IReleaseSpec spec = SelectLatestActivatedSpec(config, blockTimestamp);
        return new SingleReleaseSpecProvider(spec, networkId: chainId, chainId: chainId);
    }

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

    private static ISpecProvider GetSpecProvider(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chain id: {chainId}", nameof(chainId))
    };
}
