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

using System;
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Synchronization.Blocks
{
    [DebuggerDisplay("{Current}")]
    public struct SyncBatchSize
    {
        private ILogger _logger;

        // The batch size is kinda also used for downloading bodies which is large. Peers can return less body
        // than required, however, they still tend to timeout, so we try to limit this from our side.
        public const int Max = 128;
        public const int Min = 2;
        public const int Start = 32;

        public const double AdjustmentFactor = 1.5;

        public int Current { get; private set; }

        public bool IsMin => Current == Min;

        public bool IsMax => Current == Max;

        public SyncBatchSize(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            Current = Start;
        }

        public void Expand()
        {
            if (Current == Max)
            {
                return;
            }

            Current = Math.Min(Max, (int)Math.Ceiling(Current * AdjustmentFactor));
            if (_logger.IsDebug) _logger.Debug($"Changing sync batch size to {Current}");
        }

        public void ExpandUntilMax()
        {
            while (Current != Max)
            {
                Expand();
            }
        }

        public void Shrink()
        {
            Current = Math.Max(Min, (int)(Current / AdjustmentFactor));
            if (_logger.IsDebug) _logger.Debug($"Changing sync batch size to {Current}");
        }

        public void Reset()
        {
            Current = Start;
        }
    }
}
