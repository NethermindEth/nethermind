// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Int256;

namespace Ethereum.Difficulty.Test
{
    [DebuggerDisplay("{Name}")]
    public class DifficultyTests(
        string fileName,
        string name,
        ulong parentTimestamp,
        UInt256 parentDifficulty,
        ulong currentTimestamp,
        long currentBlockNumber,
        UInt256 currentDifficulty,
        bool parentHasUncles)
    {
        public ulong ParentTimestamp { get; set; } = parentTimestamp;
        public UInt256 ParentDifficulty { get; set; } = parentDifficulty;
        public ulong CurrentTimestamp { get; set; } = currentTimestamp;
        public long CurrentBlockNumber { get; set; } = currentBlockNumber;
        public bool ParentHasUncles { get; set; } = parentHasUncles;
        public UInt256 CurrentDifficulty { get; set; } = currentDifficulty;
        public string Name { get; set; } = name;
        public string FileName { get; set; } = fileName;

        public override string ToString() =>
            string.Concat(CurrentBlockNumber, ".", CurrentTimestamp - ParentTimestamp, ".", Name);
    }
}
