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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Mining
{  
    public class FakeSealer : ISealer, ISealValidator
    {
        private readonly TimeSpan _miningDelay;
        private readonly bool _exact;

        public FakeSealer(TimeSpan miningDelay, bool exact = true)
        {
            _miningDelay = miningDelay;
            _exact = exact;
        }

        private static readonly Random Random = new Random();

        private TimeSpan RandomizeDelay()
        {
            return _miningDelay + TimeSpan.FromMilliseconds((_exact ? 0 : 1) * (Random.Next((int)_miningDelay.TotalMilliseconds) - (int)_miningDelay.TotalMilliseconds / 2));
        }
        
        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            block.Header.MixHash = Keccak.Zero;
            block.Header.Hash = BlockHeader.CalculateHash(block.Header);

            return _miningDelay == TimeSpan.Zero
                ? Task.FromResult(block)
                : Task.Delay(RandomizeDelay(), cancellationToken)
                    .ContinueWith(t => block, cancellationToken);
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            return true;
        }

        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            return true;
        }

        public bool ValidateSeal(BlockHeader header)
        {
            return true;
        }
    }
}