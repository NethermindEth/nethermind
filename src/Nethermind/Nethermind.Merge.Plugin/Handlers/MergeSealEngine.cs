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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class MergeSealEngine : ISealEngine
    {
        private readonly ISealEngine _preMergeSealValidator;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly Address _feeRecipient;
        private readonly ISealValidator _mergeSealValidator;

        public MergeSealEngine(
            ISealEngine preMergeSealEngine,
            IPoSSwitcher? poSSwitcher,
            Address feeRecipient,
            ISealValidator mergeSealValidator,
            ILogManager? logManager)
        {
            _preMergeSealValidator =
                preMergeSealEngine ?? throw new ArgumentNullException(nameof(preMergeSealEngine));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _feeRecipient = feeRecipient;
            _mergeSealValidator = mergeSealValidator;
        }

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            if (_poSSwitcher.IsPostMerge(block.Header))
            {
                return Task.FromResult(block);
            }

            return _preMergeSealValidator.SealBlock(block, cancellationToken);
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return true;
            }

            return _preMergeSealValidator.CanSeal(blockNumber, parentHash);
        }

        public Address Address => _poSSwitcher.HasEverReachedTerminalBlock() ? _feeRecipient : _preMergeSealValidator.Address;

        public bool ValidateParams(BlockHeader parent, BlockHeader header) =>
            _preMergeSealValidator.ValidateParams(parent, header);

        public bool ValidateSeal(BlockHeader header, bool force) => _mergeSealValidator.ValidateSeal(header, force);
    }
}
