// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Verkle.Tree
{
    public class VerkleTrieStats
    {
        private const int Levels = 128;
        internal int _stateBranchCount;
        internal int _stateStemCount;
        internal int _stateLeafCount;
        internal int _missingLeaf;
        internal long _codeSize;
        internal long _stateSize;
        internal readonly int[] _stateLevels = new int[Levels];

        public int StateBranchCount => _stateBranchCount;

        public int StateStemCount => _stateStemCount;

        public int StateLeafCount => _stateLeafCount;

        public int MissingLeaf => _missingLeaf;

        public int StateCount => StateLeafCount + StateStemCount + StateBranchCount;
        public long CodeSize => _codeSize;

        public long StateSize => _stateSize;

        public long Size => StateSize + CodeSize;

        public int[] StateLevels => _stateLevels;
        public int[] AllLevels
        {
            get
            {
                int[] result = new int[Levels];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = _stateLevels[i];
                }

                return result;
            }
        }

        public override string ToString()
        {
            StringBuilder builder = new();
            builder.AppendLine("TRIE STATS");
            builder.AppendLine($"  SIZE {Size} (STATE {StateSize}, CODE {CodeSize})");
            builder.AppendLine($"  STATE NODES {StateCount} ({StateBranchCount}|{StateStemCount}|{StateLeafCount})");
            builder.AppendLine($"  MISSING {MissingLeaf}");
            builder.AppendLine($"  ALL LEVELS {string.Join(" | ", AllLevels)}");
            builder.AppendLine($"  STATE LEVELS {string.Join(" | ", StateLevels)}");
            return builder.ToString();
        }
    }
}
