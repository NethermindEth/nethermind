// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                   Validate4844Fields(transaction);
        }

        private bool Validate3860Rules(Transaction transaction, IReleaseSpec releaseSpec) => !transaction.IsAboveInitCode(releaseSpec);

        private bool ValidateTxType(Transaction transaction, IReleaseSpec releaseSpec)
        {
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
                    return false;
            }
        }

        private bool Validate1559GasFields(Transaction transaction, IReleaseSpec releaseSpec)
        {
            if (!releaseSpec.IsEip1559Enabled || !transaction.IsEip1559)
                return true;

            return transaction.MaxFeePerGas >= transaction.MaxPriorityFeePerGas;
        }

        private bool ValidateChainId(Transaction transaction)
        {
            switch (transaction.Type)
            {
                case TxType.Legacy:
                    return true;
                default:
                    return transaction.ChainId == _chainIdValue;
            }
        }

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

            return !spec.ValidateChainId || (signature.V == 27 || signature.V == 28);
        }

        private bool Validate4844Fields(Transaction transaction)
        {
            // TODO: Add blobs validation
            return transaction.Type == TxType.Blob ^ transaction.MaxFeePerDataGas is null;
        }
    }
}
