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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Logging;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]

namespace Nethermind.Mining
{
    public class AuraSealEngine : ISealEngine
    {
        private readonly ILogger _logger;

        public AuraSealEngine(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public BigInteger MinGasPrice { get; set; } = 0;

        public Task<Block> MineAsync(Block processed, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();return null;
        }

        public bool ValidateParams(Block parent, BlockHeader header)
        {
            throw new NotImplementedException();
        }

        public bool ValidateSeal(BlockHeader header)
        {
            throw new NotImplementedException();
        }

        public bool IsMining { get; set; }

        public bool Validate(BlockHeader header)
        {
            throw new NotImplementedException();
        }
    }
}