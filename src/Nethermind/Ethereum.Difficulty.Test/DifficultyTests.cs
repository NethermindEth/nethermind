// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            ulong parentTimestamp,
            UInt256 parentDifficulty,
            ulong currentTimestamp,
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

        public ulong ParentTimestamp { get; set; }
        public UInt256 ParentDifficulty { get; set; }
        public ulong CurrentTimestamp { get; set; }
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
