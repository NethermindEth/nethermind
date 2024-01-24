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
            // validate type before calculating intrinsic gas to avoid exception
            if (!ValidateTxType(transaction, releaseSpec))
            {
                error = $"InvalidTxType: Transaction type in {releaseSpec.Name} is not supported.";
                return false;
            }
            /* This is unnecessarily calculated twice - at validation and execution times. */
            if (transaction.GasLimit < IntrinsicGasCalculator.Calculate(transaction, releaseSpec))
            {
                error = $"IntrinsicGasTooLow: Gas limit is too low.";
                return false;
            }
            /* if it is a call or a transfer then we require the 'To' field to have a value
               while for an init it will be empty */
            if (!ValidateSignature(transaction, releaseSpec))
            {
                error = $"InvalidTxSignature: Signature is invalid.";
                return false;
            }
            if (!ValidateChainId(transaction))
            {
                error = $"InvalidTxChainId: Expected {_chainIdValue}, got {transaction.ChainId}.";
                return false;
            }
            if (!Validate1559GasFields(transaction, releaseSpec))
            {
                error = $"InvalidMaxPriorityFeePerGas: Cannot be higher than maxFeePerGas.";
                return false;
            }
            if (!Validate3860Rules(transaction, releaseSpec))
            {
                error = $"ContractSizeTooBig: Max initcode size exceeded.";
                return false;
            }
            return Validate4844Fields(transaction, out error);
        }

        private static bool Validate3860Rules(Transaction transaction, IReleaseSpec releaseSpec) =>
            !transaction.IsAboveInitCode(releaseSpec);

        private static bool ValidateTxType(Transaction transaction, IReleaseSpec releaseSpec) =>
            transaction.Type switch
            {
                TxType.Legacy => true,
                TxType.AccessList => releaseSpec.UseTxAccessLists,
                TxType.EIP1559 => releaseSpec.IsEip1559Enabled,
                TxType.Blob => releaseSpec.IsEip4844Enabled,
                _ => false
            };


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
                    error = $"NotAllowedMaxFeePerBlobGas: Cannot be set.";
                    return false;
                }
                if (transaction.BlobVersionedHashes is not null)
                {
                    error = $"NotAllowedBlobVersionedHashes: Cannot be set.";
                    return false;
                }
                if (transaction is { NetworkWrapper: ShardBlobNetworkWrapper })
                {
                    //This must be an internal issue?
                    error = $"InvalidTransaction: Cannot be {nameof(ShardBlobNetworkWrapper)}.";
                    return false;
                }
                error = null;
                return true;
            }

            if (transaction.To is null)
            {
                error = $"BlobTxMissingTo: Must be set.";
                return false;
            }

            if (transaction.MaxFeePerBlobGas is null)
            {
                error = $"BlobTxMissingMaxFeePerBlobGas: Must be set.";
                return false;
            }

            if (transaction.BlobVersionedHashes is null)
            {
                error = $"BlobTxMissingBlobVersionedHashes: Must be set.";
                return false;
            }

            var totalDataGas = BlobGasCalculator.CalculateBlobGas(transaction.BlobVersionedHashes!.Length);
            if (totalDataGas > Eip4844Constants.MaxBlobGasPerTransaction)
            {
                error = $"BlobTxGasLimitExceeded: Transaction exceeded {Eip4844Constants.MaxBlobGasPerTransaction}.";
                return false;
            }
            if (transaction.BlobVersionedHashes!.Length < Eip4844Constants.MinBlobsPerTransaction)
            {
                error = $"BlobTxMissingBlobs: Blob transaction must have blobs.";
                return false;
            }

            int blobCount = transaction.BlobVersionedHashes.Length;

            for (int i = 0; i < blobCount; i++)
            {
                if (transaction.BlobVersionedHashes[i] is null)
                {
                    error = $"MissingBlobVersionedHash: Must be set.";
                    return false;
                }
                if (transaction.BlobVersionedHashes![i].Length !=
                KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    error = $"InvalidBlobVersionedHashSize: Cannot exceed {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";
                    return false;
                }
                if (transaction.BlobVersionedHashes![i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                {
                    error = $"InvalidBlobVersionedHashVersion: Blob version not supported.";
                    return false;
                }
            }

            // Mempool version verification if presents
            if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
            {
                if (wrapper.Blobs.Length != blobCount)
                {
                    error = CommonMessages.InvalidBlobData();
                    return false;
                }
                if (wrapper.Commitments.Length != blobCount)
                {
                    error = CommonMessages.InvalidBlobData();
                    return false;
                }
                if (wrapper.Proofs.Length != blobCount)
                {
                    error = CommonMessages.InvalidBlobData();
                    return false;
                }

                for (int i = 0; i < blobCount; i++)
                {
                    if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob)
                    {
                        error = $"ExceededBlobSize: Cannot be more than {Ckzg.Ckzg.BytesPerBlob}.";
                        return false;
                    }
                    if (wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment)
                    {
                        error = $"ExceededBlobCommitmentSize: Cannot be more than {Ckzg.Ckzg.BytesPerCommitment}.";
                        return false;
                    }
                    if (wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
                    {
                        error = $"InvalidBlobProofSize: Cannot be more than {Ckzg.Ckzg.BytesPerProof}.";
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
                        error = $"InvalidBlobCommitmentHash: Commitment hash does not match.";
                        return false;
                    }
                }

                if (!KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs,
                    wrapper.Commitments, wrapper.Proofs))
                {
                    error = $"InvalidBlobProof: Proof does not match.";
                    return false;
                }

            }
            error = null;
            return true;
        }
    }
}
