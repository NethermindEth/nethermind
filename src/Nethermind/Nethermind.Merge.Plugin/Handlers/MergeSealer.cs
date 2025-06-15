// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class MergeSealer : ISealer
    {
        private readonly ISealer _preMergeSealer;
        private readonly IPoSSwitcher _poSSwitcher;

        public MergeSealer(
            ISealer preMergeSealer,
            IPoSSwitcher poSSwitcher)
        {
            _preMergeSealer = preMergeSealer;
            _poSSwitcher = poSSwitcher;
        }

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            if (_poSSwitcher.IsPostMerge(block.Header))
            {
                return Task.FromResult(block);
            }

            return _preMergeSealer.SealBlock(block, cancellationToken);
        }

        public bool CanSeal(long blockNumber, Hash256 parentHash)
        {
            if (_poSSwitcher.HasEverReachedTerminalBlock())
            {
                return true;
            }

            return _preMergeSealer.CanSeal(blockNumber, parentHash);
        }

        public Address Address => _poSSwitcher.HasEverReachedTerminalBlock() ? Address.Zero : _preMergeSealer.Address;
    }
}
