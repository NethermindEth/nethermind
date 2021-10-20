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
// 

using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public class PostMergeHeaderValidator : IHeaderValidator
    {
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;

        public PostMergeHeaderValidator(
            IPoSSwitcher poSSwitcher,
            IHeaderValidator headerValidator,
            ILogManager logManager)
        {
            _poSSwitcher = poSSwitcher;
            _headerValidator = headerValidator;
            _logger = logManager.GetClassLogger();
        }

        public bool ValidateHash(BlockHeader header)
        {
            return _headerValidator.ValidateHash(header);
        }

        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            bool theMergeValid = ValidateTheMergeChecks(header);
            return _headerValidator.Validate(header, parent, isUncle) && theMergeValid;
        }

        public bool Validate(BlockHeader header, bool isUncle = false)
        {
            bool theMergeValid = ValidateTheMergeChecks(header);
            return _headerValidator.Validate(header, isUncle)  && theMergeValid;
        }

        private bool ValidateTheMergeChecks(BlockHeader header)
        {
            if (_poSSwitcher.IsPos(header) == false)
                return true;
            bool validDifficulty =
                ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty));
            bool validNonce = ValidateHeaderField(header, header.Nonce, 0ul, nameof(header.Nonce));
            // validExtraData needed in previous version of EIP-3675 specification
            //bool validExtraData = ValidateHeaderField<byte>(header, header.ExtraData, Array.Empty<byte>(), nameof(header.ExtraData));
            bool validMixHash = ValidateHeaderField(header, header.MixHash, Keccak.Zero, nameof(header.MixHash));
            bool validUncles = ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp,
                nameof(header.UnclesHash));

            return validDifficulty
                   && validNonce
                   //&& validExtraData
                   && validMixHash
                   && validUncles;
        }
        
        private bool ValidateHeaderField<T>(BlockHeader header, T value, T expected, string name)
        {
            if (Equals(value, expected)) return true;
            if (_logger.IsWarn)
                _logger.Warn(
                    $"Invalid block header {header.ToString(BlockHeader.Format.Short)} - the {name} is incorrect expected {expected}, got {value} .");
            return false;
        }
    }
}
