// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
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
            // Also installs the run's hash seed, which every hash-keyed container below depends on.
            payload = InputDecoder.Decode(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
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
                BlobVersionedHashesMatch(transactions, payload.VersionedHashes.Span) &&
                HeaderValidator.ValidateHash(block.Header))
            {
                ISpecProvider specProvider = payload.SpecProvider;
                IReleaseSpec spec = specProvider.GetSpec(block.Header);
#if !ZK_EVM
                if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
                    KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
#endif
                if (TryAssignSenders(transactions, payload.EncodedTransactions, publicKeys, specProvider, spec))
                {
                    using Witness witness = payload.Witness.ToWitness();

                    // Reconstruction derives body roots; the hash check above binds them to the declared block hash.
                    success = Execute(block, witness, specProvider, validateHashes: false);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }

        if (success)
        {
            result.IsSuccess = true;
            output = StatelessValidationResult.Encode(result);
        }

        return output;
    }

    public static bool Execute(Block suggestedBlock, Witness witness, ISpecProvider specProvider)
        => Execute(suggestedBlock, witness, specProvider, validateHashes: true);

    private static bool Execute(Block suggestedBlock, Witness witness, ISpecProvider specProvider, bool validateHashes)
    {
        StatelessBlockProcessingEnv blockProcessingEnv = new(witness, specProvider, Always.Valid, NullLogManager.Instance);
        StatelessBlockProcessingResult result = blockProcessingEnv.Process(suggestedBlock, validateHashes);
        if (!result.IsValid)
        {
            Debug.WriteLine(result.Error);
        }

        return result.IsValid;
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

    /// <summary>
    /// Binds each supplied public key to the signature of the transaction at the same index and assigns the
    /// recovered sender, returning whether every key matched.
    /// </summary>
    /// <remarks>
    /// The keys are an input hint and are verified rather than trusted: comparing against the key the signature
    /// recovers also pins the recovery id, since a signature's other recovery candidate verifies just as well on
    /// its own and would name a different sender.
    /// </remarks>
    private static bool TryAssignSenders(
        Transaction[] transactions, byte[][] encodedTransactions, ReadOnlySpan<SszPublicKey> publicKeys,
        ISpecProvider specProvider, IReleaseSpec spec)
    {
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];

            if (!ecdsa.TryRecoverPublicKey(transaction, encodedTransactions[i], recovered, !spec.ValidateChainId) ||
                !publicKeys[i].AsSpan().SequenceEqual(recovered))
            {
                return false;
            }

            transaction.SenderAddress = PublicKey.ComputeAddress(recovered[1..]);
        }

        return true;
    }

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
