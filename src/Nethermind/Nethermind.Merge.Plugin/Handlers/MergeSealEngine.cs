// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly ISealValidator _mergeSealValidator;

        public MergeSealEngine(
            ISealEngine preMergeSealEngine,
            IPoSSwitcher? poSSwitcher,
            ISealValidator mergeSealValidator,
            ILogManager? logManager)
        {
            _preMergeSealValidator =
                preMergeSealEngine ?? throw new ArgumentNullException(nameof(preMergeSealEngine));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
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

        public Address Address => _poSSwitcher.HasEverReachedTerminalBlock() ? Address.Zero : _preMergeSealValidator.Address;

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle) =>
            _preMergeSealValidator.ValidateParams(parent, header, isUncle);

        public bool ValidateSeal(BlockHeader header, bool force) => _mergeSealValidator.ValidateSeal(header, force);
    }
}
