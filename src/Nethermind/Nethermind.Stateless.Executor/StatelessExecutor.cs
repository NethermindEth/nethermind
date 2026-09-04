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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static class StatelessExecutor
{
    public static byte[] Execute(ReadOnlySpan<byte> data)
    {
        // Before anything hashes a key: the guest derives its mixers' lane multipliers from this seed,
        // and installing it here rather than in a static initializer keeps a class-initialisation check
        // off every mixer call. A no-op on the host, which seeds itself per process.
        SpanExtensions.SeedHashes(SpanExtensions.DefaultHashSeed);

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

        StatelessValidationResult result = new()
        {
            NewPayloadRequestRoot = payload.NewPayloadRequestRoot,
            IsSuccess = false,
            ChainId = payload.ChainId,
            SchemaId = payload.SchemaId
        };
        output = StatelessValidationResult.Encode(result);
        bool success = false;

        // Published before block reconstruction, the first step that can throw, so a failure there
        // still reports the decoded metadata rather than the zero sentinel.
        FailureOutput = output;

        try
        {
            Block block = payload.GetBlock();
            ReadOnlySpan<SszPublicKey> publicKeys = payload.PublicKeys.Span;
            Transaction[] transactions = block.Transactions;

            if (transactions.Length == publicKeys.Length &&
                BlobVersionedHashesMatch(transactions, payload.VersionedHashes.Span))
            {
                ISpecProvider specProvider = payload.SpecProvider;
                IReleaseSpec spec = specProvider.GetSpec(block.Header);
#if !ZK_EVM
                if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
                    KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
#endif
                for (int i = 0; i < transactions.Length; i++)
                    transactions[i].SenderAddress = PublicKey.ComputeAddress(publicKeys[i].AsSpan()[1..]);

                using Witness witness = payload.Witness.ToWitness();

                success = Execute(block, witness, specProvider);
            }
        }
        catch (Exception ex)
        {
            Debug.Fail(ex.Message);
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
        ChainId = 0,
        SchemaId = 0
    };

    /// <summary>Returns whether <paramref name="transactions"/> commit to exactly <paramref name="expected"/>, in order.</summary>
    internal static bool BlobVersionedHashesMatch(Transaction[] transactions, ReadOnlySpan<Hash256> expected)
    {
        int index = 0;

        foreach (Transaction transaction in transactions)
        {
            byte[]?[]? hashes = transaction.BlobVersionedHashes;

            if (hashes is null)
                continue;

            foreach (byte[]? hash in hashes)
            {
                if (index == expected.Length || !expected[index].Bytes.SequenceEqual(hash))
                    return false;

                index++;
            }
        }

        return index == expected.Length;
    }
}
