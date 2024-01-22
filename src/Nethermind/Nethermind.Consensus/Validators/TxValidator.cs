// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators
{
    public class TxValidator : ITxValidator
    {
        private readonly ulong _chainIdValue;

        public TxValidator(ulong chainId)
        {
            _chainIdValue = chainId;
        }

        /* Full and correct validation is only possible in the context of a specific block
           as we cannot generalize correctness of the transaction without knowing the EIPs implemented
           and the world state (account nonce in particular ).
           Even without protocol change the tx can become invalid if another tx
           from the same account with the same nonce got included on the chain.
           As such we can decide whether tx is well formed but we also have to validate nonce
           just before the execution of the block / tx. */
        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
        {
            return IsWellFormed(transaction, releaseSpec, out _);
        }
        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, out string? error)
        {
            // validate type before calculating intrinsic gas to avoid exception
            return ValidateTxType(transaction, releaseSpec, out error) &&
                   /* This is unnecessarily calculated twice - at validation and execution times. */
                   transaction.GasLimit >= IntrinsicGasCalculator.Calculate(transaction, releaseSpec) &&
                   /* if it is a call or a transfer then we require the 'To' field to have a value
                      while for an init it will be empty */
                   ValidateSignature(transaction, releaseSpec) &&
                   ValidateChainId(transaction) &&
                   Validate1559GasFields(transaction, releaseSpec) &&
                   Validate3860Rules(transaction, releaseSpec) &&
                   Validate4844Fields(transaction, out error);
        }

        private static bool Validate3860Rules(Transaction transaction, IReleaseSpec releaseSpec) =>
            !transaction.IsAboveInitCode(releaseSpec);

        private static bool ValidateTxType(Transaction transaction, IReleaseSpec releaseSpec, out string? error)
        {
            error = null;
            switch (transaction.Type)
            {
                case TxType.Legacy:
                    return true;
                case TxType.AccessList:
                    return releaseSpec.UseTxAccessLists;
                case TxType.EIP1559:
                    return releaseSpec.IsEip1559Enabled;
                case TxType.Blob:
                    return releaseSpec.IsEip4844Enabled;
                default:
                    error = $"Unknown transaction type: {transaction.Type}";
                    return false;
            }
        }


        private static bool Validate1559GasFields(Transaction transaction, IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsEip1559Enabled || !transaction.Supports1559)
                return true;

            return transaction.MaxFeePerGas >= transaction.MaxPriorityFeePerGas;
        }

        private bool ValidateChainId(Transaction transaction) =>
            transaction.Type switch
            {
                TxType.Legacy => true,
                _ => transaction.ChainId == _chainIdValue
            };

        private bool ValidateSignature(Transaction tx, IReleaseSpec spec)
        {
            Signature? signature = tx.Signature;

            if (signature is null)
            {
                return false;
            }

            UInt256 sValue = new(signature.SAsSpan, isBigEndian: true);
            UInt256 rValue = new(signature.RAsSpan, isBigEndian: true);

            if (sValue.IsZero || sValue >= (spec.IsEip2Enabled ? Secp256K1Curve.HalfNPlusOne : Secp256K1Curve.N))
            {
                return false;
            }

            if (rValue.IsZero || rValue >= Secp256K1Curve.NMinusOne)
            {
                return false;
            }

            if (signature.V is 27 or 28)
            {
                return true;
            }

            if (tx.Type == TxType.Legacy && spec.IsEip155Enabled && (signature.V == _chainIdValue * 2 + 35ul || signature.V == _chainIdValue * 2 + 36ul))
            {
                return true;
            }

            return !spec.ValidateChainId;
        }

        private static bool Validate4844Fields(Transaction transaction, out string? error)
        {
            // Execution-payload version verification
            if (!transaction.SupportsBlobs)
            {
                if (transaction.MaxFeePerBlobGas is not null)
                {
                    error = $"Non blob transaction cannot have '{nameof(Transaction.MaxFeePerBlobGas)}' set.";
                    return false;
                }
                if (transaction.BlobVersionedHashes is not null)
                {
                    error = $"Non blob transaction cannot have '{nameof(Transaction.BlobVersionedHashes)}' set.";
                    return false;
                }
                if (transaction is { NetworkWrapper: ShardBlobNetworkWrapper })
                {
                    //This could be an internal problem
                    error = $"Non blob transaction cannot be {nameof(ShardBlobNetworkWrapper)}.";
                    return false;
                }
                error = null;
                return true;
            }

            if (transaction.To is null)
            {
                error = $"Transaction must have '{nameof(Transaction.To)}' set.";
                return false;
            }

            if (transaction.MaxFeePerBlobGas is null)
            {
                error = $"Blob transaction must have '{nameof(Transaction.MaxFeePerBlobGas)}' set.";
                return false;
            }

            if (transaction.BlobVersionedHashes is null)
            {
                error = $"Blob transaction must have '{nameof(Transaction.BlobVersionedHashes)}' set.";
                return false;
            }
            if (BlobGasCalculator.CalculateBlobGas(transaction.BlobVersionedHashes!.Length) > Eip4844Constants.MaxBlobGasPerTransaction)
            {
                error = $"Total transaction blob gas exceeds maximum of {Eip4844Constants.MaxBlobGasPerTransaction} per block.";
                return false;
            }
            if (transaction.BlobVersionedHashes!.Length < Eip4844Constants.MinBlobsPerTransaction)
            {
                error = $"Blob transactions must have at least {Eip4844Constants.MinBlobsPerTransaction} blob.";
                return false;
            }

            int blobCount = transaction.BlobVersionedHashes.Length;

            for (int i = 0; i < blobCount; i++)
            {
                if (transaction.BlobVersionedHashes[i] is null)
                {
                    error = $"Transaction {transaction.Hash.ToShortString()} with blob at index {i} is missing 'VersionedHash'.";
                    return false;
                }
                if (transaction.BlobVersionedHashes![i].Length !=
                KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    error = $"Transaction {transaction.Hash.ToShortString()} with blob at index {i} exceed maximum length of {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";
                    return false;
                }
                if (transaction.BlobVersionedHashes![i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                {
                    error = $"Transaction {transaction.Hash.ToShortString()} with blob at index {i} has unexpected hash version {transaction.BlobVersionedHashes![i][0]}.";
                    return false;
                }
            }

            // Mempool version verification if presents
            if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
            {
                if (wrapper.Blobs.Length != blobCount)
                {
                    error = $"Blob transactions must have an equal amount of blobs, hashes, commitments and proofs.";
                    return false;
                }
                if (wrapper.Commitments.Length != blobCount)
                {
                    error = $"Blob transactions must have an equal amount of blobs, hashes, commitments and proofs.";
                    return false;
                }
                if (wrapper.Proofs.Length != blobCount)
                {
                    error = $"Blob transactions must have an equal amount of blobs, hashes, commitments and proofs.";
                    return false;
                }

                for (int i = 0; i < blobCount; i++)
                {
                    if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob)
                    {
                        error = $"Blob at index {i} exceeds maximum size of {Ckzg.Ckzg.BytesPerBlob}.";
                        return false;
                    }
                    if (wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
                    {
                        error = $"Blob commitment at index {i} exceeds maximum size of {Ckzg.Ckzg.BytesPerCommitment}.";
                        return false;
                    }
                    if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
                    {
                        error = $"Blob proof at index {i} exceeds maximum size of {Ckzg.Ckzg.BytesPerProof}.";
                        return false;
                    }
                }

                Span<byte> hash = stackalloc byte[32];
                for (int i = 0; i < blobCount; i++)
                {
                    if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(
                            wrapper.Commitments[i].AsSpan(), hash) ||
                        !hash.SequenceEqual(transaction.BlobVersionedHashes[i]))
                    {
                        error = $"Blob commitment at index {i} is invalid.";
                        return false;
                    }
                }

                if (!KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs,
                    wrapper.Commitments, wrapper.Proofs))
                {
                    error = $"One ore more blob proofs are invalid.";
                    return false;
                }

            }
            error = null;
            return true;
        }
    }
}
