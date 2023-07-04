// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text;

namespace Nethermind.Trie
{
    public class TrieStats
    {
        private const int Levels = 128;
        internal int _stateBranchCount;
        internal int _stateExtensionCount;
        internal int _accountCount;
        internal int _storageBranchCount;
        internal int _storageExtensionCount;
        internal int _storageLeafCount;
        internal int _codeCount;
        internal int _missingState;
        internal int _missingCode;
        internal int _missingStorage;
        internal long _storageSize;
        internal long _codeSize;
        internal long _stateSize;
        internal readonly int[] _stateLevels = new int[Levels];
        internal readonly int[] _storageLevels = new int[Levels];
        internal readonly int[] _codeLevels = new int[Levels];

        public int StateBranchCount => _stateBranchCount;

        public int StateExtensionCount => _stateExtensionCount;

        public int AccountCount => _accountCount;

        public int StorageBranchCount => _storageBranchCount;

        public int StorageExtensionCount => _storageExtensionCount;

        public int StorageLeafCount => _storageLeafCount;

        public int CodeCount => _codeCount;

        public int MissingState => _missingState;

        public int MissingCode => _missingCode;

        public int MissingStorage => _missingStorage;

        public int MissingNodes => MissingCode + MissingState + MissingStorage;

        public int StorageCount => StorageLeafCount + StorageExtensionCount + StorageBranchCount;

        public int StateCount => AccountCount + StateExtensionCount + StateBranchCount;

        public int NodesCount => StorageCount + StateCount + CodeCount;

        public long StorageSize => _storageSize;

        public long CodeSize => _codeSize;

        public long StateSize => _stateSize;

        public long Size => StateSize + StorageSize + CodeSize;

        //        public List<string> MissingNodes { get; set; } = new List<string>();

        public int[] StateLevels => _stateLevels;
        public int[] StorageLevels => _storageLevels;
        public int[] CodeLevels => _codeLevels;
        public int[] AllLevels
        {
            get
            {
                int[] result = new int[Levels];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = _stateLevels[i] + _storageLevels[i] + _codeLevels[i];
                }

                return result;
            }
        }

        public override string ToString()
        {
            StringBuilder builder = new();
            builder.AppendLine("TRIE STATS");
            builder.AppendLine($"  SIZE {Size} (STATE {StateSize}, CODE {CodeSize}, STORAGE {StorageSize})");
            builder.AppendLine($"  ALL NODES {NodesCount} ({StateBranchCount + StorageBranchCount}|{StateExtensionCount + StorageExtensionCount}|{AccountCount + StorageLeafCount})");
            builder.AppendLine($"  STATE NODES {StateCount} ({StateBranchCount}|{StateExtensionCount}|{AccountCount})");
            builder.AppendLine($"  STORAGE NODES {StorageCount} ({StorageBranchCount}|{StorageExtensionCount}|{StorageLeafCount})");
            builder.AppendLine($"  ACCOUNTS {AccountCount} OF WHICH ({CodeCount}) ARE CONTRACTS");
            builder.AppendLine($"  MISSING {MissingNodes} (STATE {MissingState}, CODE {MissingCode}, STORAGE {MissingStorage})");
            builder.AppendLine($"  ALL LEVELS {string.Join(" | ", AllLevels)}");
            builder.AppendLine($"  STATE LEVELS {string.Join(" | ", StateLevels)}");
            builder.AppendLine($"  STORAGE LEVELS {string.Join(" | ", StorageLevels)}");
            builder.AppendLine($"  CODE LEVELS {string.Join(" | ", CodeLevels)}");
            return builder.ToString();
        }
    }
}
