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
            // validate type before calculating intrinsic gas to avoid exception
            return ValidateTxType(transaction, releaseSpec) &&
                   /* This is unnecessarily calculated twice - at validation and execution times. */
                   transaction.GasLimit >= IntrinsicGasCalculator.Calculate(transaction, releaseSpec) &&
                   /* if it is a call or a transfer then we require the 'To' field to have a value
                      while for an init it will be empty */
                   ValidateSignature(transaction.Signature, releaseSpec) &&
                   ValidateChainId(transaction) &&
                   Validate1559GasFields(transaction, releaseSpec) &&
                   Validate3860Rules(transaction, releaseSpec) &&
                   Validate4844Fields(transaction, releaseSpec);
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

        private bool ValidateSignature(Signature? signature, IReleaseSpec spec)
        {
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

            if (spec.IsEip155Enabled)
            {
                return (signature.ChainId ?? _chainIdValue) == _chainIdValue;
            }

            return !spec.ValidateChainId || signature.V is 27 or 28;
        }

        private static bool Validate4844Fields(Transaction transaction, IReleaseSpec spec)
        {
            // Execution-payload version verification
            if (!transaction.SupportsBlobs)
            {
                return transaction.MaxFeePerDataGas is null &&
                       transaction.BlobVersionedHashes is null &&
                       transaction is not { NetworkWrapper: ShardBlobNetworkWrapper };
            }

            if (transaction.MaxFeePerDataGas is null ||
                transaction.BlobVersionedHashes is null ||
                IntrinsicGasCalculator.CalculateDataGas(transaction.BlobVersionedHashes!.Length) > Eip4844Constants.MaxDataGasPerTransaction ||
                transaction.BlobVersionedHashes!.Length < Eip4844Constants.MinBlobsPerTransaction)
            {
                return false;
            }

            for (int i = 0; i < transaction.BlobVersionedHashes!.Length; i++)
            {
                if (transaction.BlobVersionedHashes[i] is null ||
                    transaction.BlobVersionedHashes![i].Length !=
                    KzgPolynomialCommitments.BytesPerBlobVersionedHash ||
                    transaction.BlobVersionedHashes![i][0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                {
                    return false;
                }
            }

            // Mempool version verification if presents
            if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
            {
                int blobCount = wrapper.Blobs.Length / Ckzg.Ckzg.BytesPerBlob;
                if (transaction.BlobVersionedHashes.Length != blobCount ||
                    (wrapper.Commitments.Length / Ckzg.Ckzg.BytesPerCommitment) != blobCount ||
                    (wrapper.Proofs.Length / Ckzg.Ckzg.BytesPerProof) != blobCount)
                {
                    return false;
                }

                Span<byte> hash = stackalloc byte[32];
                Span<byte> commitements = wrapper.Commitments;
                for (int i = 0, n = 0;
                     i < transaction.BlobVersionedHashes.Length;
                     i++, n += Ckzg.Ckzg.BytesPerCommitment)
                {
                    if (!KzgPolynomialCommitments.TryComputeCommitmentHashV1(
                            commitements[n..(n + Ckzg.Ckzg.BytesPerCommitment)], hash) ||
                        !hash.SequenceEqual(transaction.BlobVersionedHashes[i]))
                    {
                        return false;
                    }
                }

                return KzgPolynomialCommitments.AreProofsValid(wrapper.Blobs,
                    wrapper.Commitments, wrapper.Proofs);
            }

            return true;
        }
    }
}
