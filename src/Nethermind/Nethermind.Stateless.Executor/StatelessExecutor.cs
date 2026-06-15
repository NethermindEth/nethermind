// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    /// <remarks>
    /// As there's no exception unwinding in zkVM runtimes, an exception thrown during execution
    /// never reaches the catch block in <see cref="Execute(ReadOnlySpan{byte})"/>
    /// — the runtime invokes the guest's <c>ZkvmThrow</c> callback instead.
    /// Therefore, the execution output is stored here for the error handler to access.
    /// </remarks>
    public static ReadOnlyMemory<byte> Output { get; private set; }

    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        StatelessPayload payload = InputDecoder.Decode(data);
        ReadOnlySpan<SszPublicKeys> publicKeys = payload.PublicKeys.Span;
        Transaction[] transactions = payload.Block.Transactions;
        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.NewPayloadRequestRoot,
            IsSuccess = false,
            ChainConfig = payload.ChainConfig
        };
        byte[] output = StatelessValidationResult.Encode(result);
        bool success = false;
        Output = output;

        if (transactions.Length == publicKeys.Length)
        {
            try
            {
                ISpecProvider specProvider = GetSpecProvider(payload.ChainConfig);
                IReleaseSpec spec = specProvider.GetSpec(payload.Block.Header);
#if !ZK_EVM
                if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
                    KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
#endif
                for (int i = 0; i < transactions.Length; i++)
                    transactions[i].SenderAddress = PublicKey.ComputeAddress(publicKeys[i].Bytes.AsSpan(1));

                using Witness witness = payload.Witness.ToWitness();

                success = Execute(payload.Block, witness, specProvider);
            }
            catch (Exception ex)
            {
                Debug.Fail(ex.Message);
            }
        }

        if (success)
            // Flip the success flag in SSZ instead of re-encoding the entire output (not future-proof)
            output[32] = 1;

        return output;
    }

    public static bool Execute(Block suggestedBlock, Witness witness, ISpecProvider specProvider)
    {
        using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
        BlockHeader parentHeader;

        // The parent header must be the last one in the list
        // and must match the parent hash of the suggested block
        if (headers.Count > 0 && suggestedBlock.Header.ParentHash == headers[^1].Hash)
        {
            parentHeader = headers[^1];
        }
        else
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

    private static ISpecProvider GetSpecProvider(ChainConfig chainConfig)
    {
        if (!ChainSpecBasedSpecProvider.KnownProvidersByChainId.TryGetValue(chainConfig.ChainId, out IForkAwareSpecProvider? baseProvider))
            throw new ArgumentException($"Unknown chain id: {chainConfig.ChainId}", nameof(chainConfig));

        // Empty arrays mean ActiveFork was omitted — use the base provider as-is.
        if (chainConfig.ActiveFork.Fork == 0 &&
            chainConfig.ActiveFork.Activation.BlockNumber.Length == 0 &&
            chainConfig.ActiveFork.Activation.Timestamp.Length == 0)
        {
            return baseProvider;
        }

        return StatelessSpecProvider.Create(baseProvider, chainConfig.ActiveFork);
    }
}
