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

using System;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.AuRa
{
    public class AuRaSealValidator : ISealValidator
    {
        private readonly IAuRaValidator _validator;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;

        public AuRaSealValidator(IAuRaValidator validator, IEthereumEcdsa ecdsa, ILogManager logManager)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            if (header.AuRaStep < parent.AuRaStep)
            {
                return false;
            }
            
            if (header.AuRaStep - parent.AuRaStep != 1)
            {
                // report_skipped
            }
            
            return true;
        }

        public bool ValidateSeal(BlockHeader header)
        {
            if (header.IsGenesis) return true;
            
            if (header.Author == null)
            {
                header.Author = GetSealer(header);
            }

            // check if valid author for step(!)
            
            bool isValid = header.Author == header.Beneficiary;
            // cannot call: _validator.IsValidSealer(header.Author); because we can call it only when previous step was processed.
            
            return isValid;
            // report_benign
        }

        private Address GetSealer(BlockHeader header)
        {
            Signature signature = new Signature(header.AuRaSignature);
            signature.V += 27;
            Keccak message = BlockHeader.CalculateHash(header, RlpBehaviors.ForSealing);
            return _ecdsa.RecoverAddress(signature, message);
        }
    }
}