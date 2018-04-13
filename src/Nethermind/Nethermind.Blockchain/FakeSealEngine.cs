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

namespace Nethermind.Blockchain
{
    public class FakeSealEngine : ISealEngine
    {
        private readonly TimeSpan _miningDelay;

        public FakeSealEngine(TimeSpan miningDelay)
        {
            _miningDelay = miningDelay;
        }
        
        public Task<Block> MineAsync(Block block, CancellationToken cancellationToken)
        {
            return _miningDelay == TimeSpan.Zero
                ? Task.FromResult(block)
                : Task.Delay(_miningDelay, cancellationToken).ContinueWith(t => block, cancellationToken);
        }

        public bool Validate(BlockHeader header)
        {
            return true;
        }

        public bool IsMining { get; set; }
    }
}