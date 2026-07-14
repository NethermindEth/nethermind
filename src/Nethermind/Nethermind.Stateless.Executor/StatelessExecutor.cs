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
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        byte[] output = StatelessValidationResult.Encode(_defaultFailureResult);
        FailureOutput = output;
        StatelessPayload payload;

        try
        {
            payload = InputDecoder.Decode(data);
        }
        catch (Exception ex)
        {
            Debug.Fail(ex.Message);
            return output;
        }

        ReadOnlySpan<SszPublicKeys> publicKeys = payload.PublicKeys.Span;
        Transaction[] transactions = payload.Block.Transactions;
        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.NewPayloadRequestRoot,
            IsSuccess = false,
            ChainConfig = payload.ChainConfig
        };
        output = StatelessValidationResult.Encode(result);
        bool success = false;

        FailureOutput = output;

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
        {
            result.IsSuccess = true;
            output = StatelessValidationResult.Encode(result);
        }

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

    /// <summary>
    /// Gets the encoded failure result of the current execution. Intended for zkVM guests.
    /// </summary>
    /// <remarks>
    /// As there's no exception unwinding in the zkVM runtime, an exception thrown during execution
    /// never reaches the catch block in <see cref="Execute(ReadOnlySpan{byte})"/>;
    /// instead, the runtime invokes the guest's <c>ZkvmThrow</c> callback.
    /// The failure result is therefore encoded up front, before execution begins, so the
    /// callback can access it.
    /// </remarks>
    public static ReadOnlyMemory<byte> FailureOutput { get; private set; }

    private static readonly StatelessValidationResult _defaultFailureResult = new()
    {
        NewPayloadRequestRoot = Hash256.Zero,
        IsSuccess = false,
        ChainConfig = new ChainConfig
        {
            ChainId = 0,
            ActiveFork = new ForkConfig
            {
                Fork = 0,
                Activation = new() { BlockNumber = [], Timestamp = [] },
                BlobSchedule = []
            }
        }
    };

    private static ISpecProvider GetSpecProvider(ChainConfig chainConfig)
    {
        ChainSpecBasedSpecProvider.KnownProvidersByChainId.TryGetValue(chainConfig.ChainId, out IForkAwareSpecProvider? baseProvider);

        // No ActiveFork: nothing to pin, so use the chain's own schedule; an unknown chain id can't proceed.
        if (chainConfig.ActiveFork.Fork == 0 &&
            chainConfig.ActiveFork.Activation.BlockNumber.Length == 0 &&
            chainConfig.ActiveFork.Activation.Timestamp.Length == 0)
        {
            return baseProvider ?? throw new ArgumentException($"Unknown chain id: {chainConfig.ChainId}", nameof(chainConfig));
        }

        // ActiveFork pins the spec by name on any compatible schedule; unknown chains (e.g. devnets) use Mainnet rules.
        baseProvider ??= MainnetSpecProvider.Instance;

        return StatelessSpecProvider.Create(baseProvider, chainConfig.ChainId, chainConfig.ActiveFork);
    }
}
