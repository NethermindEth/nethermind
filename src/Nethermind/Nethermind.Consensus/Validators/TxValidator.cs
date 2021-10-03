//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Validators
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
                   Validate1559GasFields(transaction, releaseSpec);
        }

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
                case TxType.AccessList:
                case TxType.EIP1559:
                    return transaction.ChainId == _chainIdValue;
                default:
                    return false;
            }
        }
        
        private bool ValidateSignature(Signature? signature, IReleaseSpec spec)
        {
            if (signature is null)
            {
                return false;
            }
            
            BigInteger sValue = signature.SAsSpan.ToUnsignedBigInteger();
            BigInteger rValue = signature.RAsSpan.ToUnsignedBigInteger();
            
            if (sValue.IsZero || sValue >= (spec.IsEip2Enabled ? Secp256K1Curve.HalfN + 1 : Secp256K1Curve.N))
            {
                return false;
            }

            if (rValue.IsZero || rValue >= Secp256K1Curve.N - 1)
            {
                return false;
            }
            
            if (spec.IsEip155Enabled)
            {
                return (signature.ChainId ?? _chainIdValue) == _chainIdValue;
            }

            return !spec.ValidateChainId || (signature.V == 27 || signature.V == 28);
        }
    }
}
