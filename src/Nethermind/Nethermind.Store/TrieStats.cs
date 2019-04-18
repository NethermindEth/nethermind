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

using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Crypto;

namespace Nethermind.Store
{
    public class TrieStats
    {
        public int StateBranchCount { get; internal set; }

        public int StateExtensionCount { get; internal set; }

        public int AccountCount { get; internal set; }

        public int StorageBranchCount { get; internal set; }

        public int StorageExtensionCount { get; internal set; }

        public int StorageLeafCount { get; internal set; }

        public int CodeCount { get; internal set; }
        
        public int MissingState { get; internal set; }
        
        public int MissingCode { get; internal set; }
        
        public int MissingStorage { get; internal set; }

        public int MissingNodes => MissingCode + MissingState + MissingStorage;

        public int StorageCount => StorageLeafCount + StorageExtensionCount + StorageBranchCount;

        public int StateCount => AccountCount + StateExtensionCount + StateBranchCount;

        public int NodesCount => StorageCount + StateCount + CodeCount;

//        public List<string> MissingNodes { get; set; } = new List<string>();

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("TRIE STATS");
            builder.AppendLine($"  ALL NODES {NodesCount} ({StateBranchCount + StorageBranchCount}|{StateExtensionCount + StorageExtensionCount}|{AccountCount + StorageLeafCount})");
            builder.AppendLine($"  STATE NODES {StateCount} ({StateBranchCount}|{StateExtensionCount}|{AccountCount})");
            builder.AppendLine($"  STORAGE NODES {StorageCount} ({StorageBranchCount}|{StorageExtensionCount}|{StorageLeafCount})");
            builder.AppendLine($"  ACCOUNTS {AccountCount} OF WHICH ({CodeCount}) ARE CONTRACTS");
            builder.AppendLine($"  MISSING STATE {MissingState}, CODE {MissingCode}, STORAGE {MissingStorage}");
            return builder.ToString();
        }
    }
}