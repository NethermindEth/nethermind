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

using System.Text;

namespace Nethermind.Trie
{
    public class TrieStats
    {
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
            return builder.ToString();
        }
    }
}
