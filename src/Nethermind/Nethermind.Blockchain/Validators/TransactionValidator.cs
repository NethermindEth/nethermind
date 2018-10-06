/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Validators
{
    public class TransactionValidator : ITransactionValidator
    {
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator;
        private readonly ISignatureValidator _signatureValidator;

        public TransactionValidator(ISignatureValidator signatureValidator)
        {
            _signatureValidator = signatureValidator;
            _intrinsicGasCalculator = new IntrinsicGasCalculator();
        }

        /* Full and orrect validation is only possible in the context of a specific block
           as we cannot generalize correctness of the transaction without knowing the EIPs implemented
           and the world state (account nonce in particular ).
           Even without protocol change the tx can become invalid if another tx
           from the same account with the same nonce got included on the chain.
           As such we can decide whether tx is well formed but we also have to validate nonce
           just before the execution of the block / tx. */
        public bool IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec, bool ignoreSignature = false)
        {
            return 
                   Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   /* This is unnecessarilly calculated twice - at validation and execution times. */
                   transaction.GasLimit >= _intrinsicGasCalculator.Calculate(transaction, releaseSpec) &&
                   /* if it is a call or a transfer then we require the 'To' field to have a value
                      while for an init it will be empty */
                   (transaction.To != null || transaction.Init != null) &&
                   Validator.IsInP256(transaction.Value) &&
                   /* can be a simple transfer, a call, or an init but not both an init and a call */
                   !(transaction.Data != null && transaction.Init != null) &&
                   (ignoreSignature || _signatureValidator.Validate(transaction.Signature, releaseSpec));
        }
    }
}