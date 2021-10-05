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

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin
{
    public class MergeHeaderValidator : HeaderValidator
    {
        private readonly IHeaderValidator _beforeTheMerge;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeHeaderValidator(
            IHeaderValidator? beforeTheMerge,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            IPoSSwitcher? poSSwitcher,
            ILogManager logManager)
            : base(blockTree, Always.Valid, specProvider, logManager)
        {
            _beforeTheMerge = beforeTheMerge ?? throw new ArgumentNullException(nameof(beforeTheMerge));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
        }

        public override bool ValidateHash(BlockHeader header)
        {
            if (_poSSwitcher.IsPos(header))
            {
                return base.ValidateHash(header);
            }

            return _beforeTheMerge.ValidateHash(header);
        }

        public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            if (_poSSwitcher.IsPos(header))
            {
                return base.Validate(header, parent, isUncle);
            }

            bool theMergeValid = ValidateTheMergeChecks(header);
            return _beforeTheMerge.ValidateHash(header) && theMergeValid;
        }

        private bool ValidateTheMergeChecks(BlockHeader header)
        {
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
    }
}
