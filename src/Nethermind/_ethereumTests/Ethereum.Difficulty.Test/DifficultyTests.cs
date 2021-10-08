/*
 * Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Diagnostics;
using Nethermind.Int256;

namespace Ethereum.Difficulty.Test
{
    [DebuggerDisplay("{Name}")]
    public class DifficultyTests
    {
        public DifficultyTests(
            string fileName,
            string name,
            UInt256 parentTimestamp,
            UInt256 parentDifficulty,
            UInt256 currentTimestamp,
            long currentBlockNumber,
            UInt256 currentDifficulty,
            bool parentHasUncles)
        {
            Name = name;
            FileName = fileName;
            ParentTimestamp = parentTimestamp;
            ParentDifficulty = parentDifficulty;
            CurrentTimestamp = currentTimestamp;
            CurrentDifficulty = currentDifficulty;
            CurrentBlockNumber = currentBlockNumber;
            ParentHasUncles = parentHasUncles;
        }

        public UInt256 ParentTimestamp { get; set; }
        public UInt256 ParentDifficulty { get; set; }
        public UInt256 CurrentTimestamp { get; set; }
        public long CurrentBlockNumber { get; set; }
        public bool ParentHasUncles { get; set; }
        public UInt256 CurrentDifficulty { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }

        public override string ToString()
        {
            return string.Concat(CurrentBlockNumber, ".", CurrentTimestamp - ParentTimestamp, ".", Name);
        }
    }
}
