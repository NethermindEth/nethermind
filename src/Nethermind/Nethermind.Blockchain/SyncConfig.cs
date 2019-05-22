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

using Nethermind.Core;

namespace Nethermind.Blockchain
{
    [Todo(Improve.Refactor, "Rename to SyncConfig")]
    public class SyncConfig : ISyncConfig
    {
        public bool ValidateTree { get; set; } = false;
        public bool FastSync { get; set; } = false;
        public int SyncTimerInterval { get; set; } = 1000;
        public int SyncPeersMaxCount { get; set; } = 25;
        public long MinAvailableBlockDiffForSyncSwitch { get; } = 100;
        public long MinDiffPercentageForLatencySwitch { get; } = 10;
        public long MinDiffForLatencySwitch { get; } = 5;
        public string PivotTotalDifficulty { get; set; } = null;
        public string PivotNumber { get; set;} = null;
        public string PivotHash { get; set;} = null;
    }
}