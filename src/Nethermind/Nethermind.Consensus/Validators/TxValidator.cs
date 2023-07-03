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
                   ValidateSignature(transaction, releaseSpec) &&
                   ValidateChainId(transaction) &&
                   Validate1559GasFields(transaction, releaseSpec) &&
                   Validate3860Rules(transaction, releaseSpec) &&
                   Validate4844Fields(transaction);
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

        private static bool Validate4844Fields(Transaction transaction)
        {
            // Execution-payload version verification
            if (!transaction.SupportsBlobs)
            {
                return transaction.MaxFeePerDataGas is null &&
                       transaction.BlobVersionedHashes is null &&
                       transaction is not { NetworkWrapper: ShardBlobNetworkWrapper };
            }

            if (transaction.To is null ||
                transaction.MaxFeePerDataGas is null ||
                transaction.BlobVersionedHashes is null ||
                transaction.BlobVersionedHashes!.Length > Eip4844Constants.MaxBlobsPerTransaction ||
                transaction.BlobVersionedHashes!.Length < Eip4844Constants.MinBlobsPerTransaction)
            {
                return false;
            }

            int blobCount = transaction.BlobVersionedHashes.Length;

            for (int i = 0; i < blobCount; i++)
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
                if (wrapper.Blobs.Length != blobCount ||
                    wrapper.Commitments.Length != blobCount ||
                    wrapper.Proofs.Length != blobCount)
                {
                    return false;
                }

                for (int i = 0; i < blobCount; i++)
                {
                    if (wrapper.Blobs[i].Length != Ckzg.Ckzg.BytesPerBlob ||
                        wrapper.Commitments[i].Length != Ckzg.Ckzg.BytesPerCommitment ||
                        wrapper.Proofs[i].Length != Ckzg.Ckzg.BytesPerProof)
                    {
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
