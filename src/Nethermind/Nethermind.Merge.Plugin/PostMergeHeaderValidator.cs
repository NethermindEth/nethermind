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
    public sealed class PostMergeHeaderValidator : HeaderValidator
    {
        private readonly IPoSSwitcher _poSSwitcher;

        public PostMergeHeaderValidator(
            IPoSSwitcher poSSwitcher,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ISealValidator sealValidator,
            ILogManager logManager)
            : base(blockTree, sealValidator, specProvider, logManager)
        {
            _poSSwitcher = poSSwitcher;
        }

        public override bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
        {
            bool theMergeValid = ValidateTheMergeChecks(header, parent);
            return base.Validate(header, parent, isUncle) && theMergeValid;
        }

        private bool ValidateTheMergeChecks(BlockHeader header, BlockHeader? parent)
        {
            bool validDifficulty = true, validNonce = true, validUncles = true;
            (bool IsTerminal, bool IsPostMerge) switchInfo = _poSSwitcher.GetBlockSwitchInfo(header, parent);
            bool terminalTotalDifficultyChecks = ValidateTerminalTotalDifficultyChecks(header, switchInfo.IsTerminal);
            if (switchInfo.IsPostMerge)
            {

                validDifficulty =
                    ValidateHeaderField(header, header.Difficulty, UInt256.Zero, nameof(header.Difficulty));
                validNonce = ValidateHeaderField(header, header.Nonce, 0u, nameof(header.Nonce));
                // validExtraData needed in previous version of EIP-3675 specification
                //bool validExtraData = ValidateHeaderField<byte>(header, header.ExtraData, Array.Empty<byte>(), nameof(header.ExtraData));
                validUncles = ValidateHeaderField(header, header.UnclesHash, Keccak.OfAnEmptySequenceRlp,
                    nameof(header.UnclesHash));
            }

            return terminalTotalDifficultyChecks
                   && validDifficulty
                   && validNonce
                   //&& validExtraData
                   && validUncles;
        }

        private bool ValidateTerminalTotalDifficultyChecks(BlockHeader header, bool isTerminal)
        {
            if (header.TotalDifficulty == null || _poSSwitcher.TerminalTotalDifficulty == null)
                return true;
            
            bool isValid = true;
            bool isPostMerge = header.IsPostMerge;
            if (isPostMerge == false && isTerminal == false)
            {
                if (header.TotalDifficulty >= _poSSwitcher.TerminalTotalDifficulty)
                    isValid = false;
            }
            else
            {
                if (header.TotalDifficulty < _poSSwitcher.TerminalTotalDifficulty)
                    isValid = false;
            }

            if (isValid == false)
            {
                if (_logger.IsWarn)
                    _logger.Warn(
                        $"Invalid block header {header.ToString(BlockHeader.Format.Short)} - total difficulty is incorrect because of TTD, TerminalTotalDifficulty: {_poSSwitcher.TerminalTotalDifficulty}, IsPostMerge {header.IsPostMerge} IsTerminalBlock: {isTerminal}");
            }

            return isValid;
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
