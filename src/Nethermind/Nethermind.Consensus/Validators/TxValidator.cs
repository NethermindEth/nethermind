// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
            error = null;

            // validate type before calculating intrinsic gas to avoid exception
            return ValidateTxType(transaction, releaseSpec, ref error)
                   // This is unnecessarily calculated twice - at validation and execution times.
                   && ValidateWithError(transaction.GasLimit < IntrinsicGasCalculator.Calculate(transaction, releaseSpec), TxErrorMessages.IntrinsicGasTooLow, ref error)
                   // if it is a call or a transfer then we require the 'To' field to have a value while for an init it will be empty
                   && ValidateWithError(ValidateSignature(transaction, releaseSpec), TxErrorMessages.InvalidTxSignature, ref error)
                   && ValidateChainId(transaction, ref error)
                   && ValidateWithError(Validate1559GasFields(transaction, releaseSpec), TxErrorMessages.InvalidMaxPriorityFeePerGas, ref error)
                   && ValidateWithError(Validate3860Rules(transaction, releaseSpec), TxErrorMessages.ContractSizeTooBig, ref error)
                   && Validate4844Fields(transaction, ref error);
        }

        private static bool Validate3860Rules(Transaction transaction, IReleaseSpec releaseSpec) =>
            !transaction.IsAboveInitCode(releaseSpec);

        private static bool ValidateTxType(Transaction transaction, IReleaseSpec releaseSpec, ref string error)
        {
            bool result = transaction.Type switch
            {
                TxType.Legacy => true,
                TxType.AccessList => releaseSpec.UseTxAccessLists,
                TxType.EIP1559 => releaseSpec.IsEip1559Enabled,
                TxType.Blob => releaseSpec.IsEip4844Enabled,
                _ => false
            };

            if (!result)
            {
                error = TxErrorMessages.InvalidTxType(releaseSpec.Name);
                return false;
            }

            return true;
        }


        private static bool Validate1559GasFields(Transaction transaction, IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsEip1559Enabled || !transaction.Supports1559)
                return true;

            return transaction.MaxFeePerGas >= transaction.MaxPriorityFeePerGas;
        }

        private bool ValidateChainId(Transaction transaction, ref string? error)
        {
            return transaction.Type switch
            {
                TxType.Legacy => true,
                _ => ValidateChainIdNonLegacy(transaction.ChainId, ref error)
            };

            bool ValidateChainIdNonLegacy(ulong? chainId, ref string? error)
            {
                bool result = chainId == _chainIdValue;
                if (!result)
                {
                    error = TxErrorMessages.InvalidTxChainId(_chainIdValue, transaction.ChainId);
                    return false;
                }

                return true;
            }
        }

        private bool ValidateWithError(bool validation, string errorMessage, ref string? error)
        {
            if (!validation)
            {
                error = errorMessage;
                return false;
            }

            return true;
        }

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

        private static bool Validate4844Fields(Transaction transaction, ref string? error)
        {
            // Execution-payload version verification
            if (!transaction.SupportsBlobs)
            {
                if (transaction.MaxFeePerBlobGas is not null)
                {
                    error = TxErrorMessages.NotAllowedMaxFeePerBlobGas;
                    return false;
                }

                if (transaction.BlobVersionedHashes is not null)
                {
                    error = TxErrorMessages.NotAllowedBlobVersionedHashes;
                    return false;
                }

                if (transaction is { NetworkWrapper: ShardBlobNetworkWrapper })
                {
                    //This must be an internal issue?
                    error = TxErrorMessages.InvalidTransaction;
                    return false;
                }

                return true;
            }

            if (transaction.To is null)
            {
                error = TxErrorMessages.TxMissingTo;
                return false;
            }

            if (transaction.MaxFeePerBlobGas is null)
            {
                error = TxErrorMessages.BlobTxMissingMaxFeePerBlobGas;
                return false;
            }

            if (transaction.BlobVersionedHashes is null)
            {
                error = TxErrorMessages.BlobTxMissingBlobVersionedHashes;
                return false;
            }

            var totalDataGas = BlobGasCalculator.CalculateBlobGas(transaction.BlobVersionedHashes!.Length);
            if (totalDataGas > Eip4844Constants.MaxBlobGasPerTransaction)
            {
                error = TxErrorMessages.BlobTxGasLimitExceeded;
                return false;
            }
            if (transaction.BlobVersionedHashes!.Length < Eip4844Constants.MinBlobsPerTransaction)
            {
                error = TxErrorMessages.BlobTxMissingBlobs;
                return false;
            }

            int blobCount = transaction.BlobVersionedHashes.Length;

            for (int i = 0; i < blobCount; i++)
            {
                if (transaction.BlobVersionedHashes[i] is null)
                {
                    error = TxErrorMessages.MissingBlobVersionedHash;
                    return false;
                }
                if (transaction.BlobVersionedHashes![i].Length !=
                KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    error = TxErrorMessages.InvalidBlobVersionedHashSize;
                    return false;
                }
                if (transaction.BlobVersionedHashes![i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                {
                    error = TxErrorMessages.InvalidBlobVersionedHashVersion;
                    return false;
                }
            }

            // Mempool version verification if presents
            if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
            {
                if (wrapper.Blobs.Length != blobCount)
                {
                    error = TxErrorMessages.InvalidBlobData;
                    return false;
                }
                if (wrapper.Commitments.Length != blobCount)
                {
                    error = TxErrorMessages.InvalidBlobData;
                    return false;
                }
                if (wrapper.Proofs.Length != blobCount)
                {
                    error = TxErrorMessages.InvalidBlobData;
                    return false;
                }

                for (int i = 0; i < blobCount; i++)
                {
                    if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob)
                    {
                        error = TxErrorMessages.ExceededBlobSize;
                        return false;
                    }
                    if (wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
                    {
                        error = TxErrorMessages.ExceededBlobCommitmentSize;
                        return false;
                    }
                    if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
                    {
                        error = TxErrorMessages.InvalidBlobProofSize;
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
                        error = TxErrorMessages.InvalidBlobCommitmentHash;
                        return false;
                    }
                }

                if (!KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs,
                    wrapper.Commitments, wrapper.Proofs))
                {
                    error = TxErrorMessages.InvalidBlobProof;
                    return false;
                }

            }

            return true;
        }
    }
}
