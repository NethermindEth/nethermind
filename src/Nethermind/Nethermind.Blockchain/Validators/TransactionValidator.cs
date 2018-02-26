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
using Nethermind.Core.Potocol;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Validators
{
    public class TransactionValidator : ITransactionValidator
    {
        private readonly IEthereumRelease _spec;
        private readonly ISignatureValidator _signatureValidator;

        // TODO: this will be calculated twice, refactor
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator = new IntrinsicGasCalculator();

        public TransactionValidator(IEthereumRelease spec, ISignatureValidator signatureValidator)
        {
            _spec = spec;
            _signatureValidator = signatureValidator;
        }

        public bool IsWellFormed(Transaction transaction, bool ignoreSignature = false)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   transaction.GasLimit >= _intrinsicGasCalculator.Calculate(_spec, transaction) &&
                   (transaction.To != null || transaction.Init != null) && // TODO: check tests where this is the case and still state changes (is the gas substracted?)
                   Validator.IsInP256(transaction.Value) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null) &&
                   (ignoreSignature || _signatureValidator.Validate(transaction.Signature));
            // TODO: also check if nonce is equal to sending account nonce
        }
    }
}