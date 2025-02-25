// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Trie
{
    public class TrieStats
    {
        private const int Levels = 128;
        internal long _stateBranchCount;
        internal long _stateExtensionCount;
        internal long _accountCount;
        internal long _storageBranchCount;
        internal long _storageExtensionCount;
        internal long _storageLeafCount;
        internal long _codeCount;
        internal long _missingState;
        internal long _missingCode;
        internal long _missingStorage;
        internal long _storageSize;
        internal long _codeSize;
        internal long _stateSize;
        internal readonly long[] _stateLevels = new long[Levels];
        internal readonly long[] _storageLevels = new long[Levels];
        internal readonly long[] _codeLevels = new long[Levels];

        public long StateBranchCount => _stateBranchCount;

        public long StateExtensionCount => _stateExtensionCount;

        public long AccountCount => _accountCount;

        public long StorageBranchCount => _storageBranchCount;

        public long StorageExtensionCount => _storageExtensionCount;

        public long StorageLeafCount => _storageLeafCount;

        public long CodeCount => _codeCount;

        public long MissingState => _missingState;

        public long MissingCode => _missingCode;

        public long MissingStorage => _missingStorage;

        public long MissingNodes => MissingCode + MissingState + MissingStorage;

        public long StorageCount => StorageLeafCount + StorageExtensionCount + StorageBranchCount;

        public long StateCount => AccountCount + StateExtensionCount + StateBranchCount;

        public long NodesCount => StorageCount + StateCount + CodeCount;

        public long StorageSize => _storageSize;

        public long CodeSize => _codeSize;

        public long StateSize => _stateSize;

        public long Size => StateSize + StorageSize + CodeSize;

        //        public List<string> MissingNodes { get; set; } = new List<string>();

        public long[] StateLevels => _stateLevels;
        public long[] StorageLevels => _storageLevels;
        public long[] CodeLevels => _codeLevels;
        public long[] AllLevels
        {
            get
            {
                long[] result = new long[Levels];
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
