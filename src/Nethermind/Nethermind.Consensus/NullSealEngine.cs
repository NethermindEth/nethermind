// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus
{
    public class NullSealEngine : ISealEngine
    {
        private NullSealEngine()
        {
        }

        public static NullSealEngine Instance { get; } = new();

        public Address Address => Address.Zero;

        public Task<Block> SealBlock(Block? block, CancellationToken cancellationToken)
        {
            return Task.FromResult(block);
        }

        public bool CanSeal(long blockNumber, Hash256? parentHash)
        {
            return true;
        }

        public bool ValidateParams(BlockHeader? parent, BlockHeader? header, bool isUncle = false)
        {
            return true;
        }

        public bool ValidateSeal(BlockHeader? header, bool force)
        {
            return true;
        }
    }
}
