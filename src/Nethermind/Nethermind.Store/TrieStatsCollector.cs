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
using Nethermind.Core.Crypto;

namespace Nethermind.Store
{
    public class TrieStatsCollector : ITreeVisitor
    {
        public TrieStats Stats { get; } = new TrieStats();
        
        public void VisitTree(Keccak rootHash, VisitContext context)
        {
        }

        public void VisitMissingNode(Keccak nodeHash, VisitContext context)
        {
            Stats.MissingNodes.Add(context.IsStorage ? $"LEVEL {context.Level} STORAGE {nodeHash}" : $"{context.Level} STATE {nodeHash}");
        }

        public void VisitBranch(Keccak nodeHash, VisitContext context)
        {
            if (context.IsStorage)
            {
                Stats.StorageBranchCount++;
            }
            else
            {
                Stats.StateBranchCount++;
            }
        }

        public void VisitExtension(Keccak nodeHash, VisitContext context)
        {
            if (context.IsStorage)
            {
                Stats.StorageExtensionCount++;
            }
            else
            {
                Stats.StateExtensionCount++;
            }
        }
        
        public void VisitLeaf(Keccak nodeHash, VisitContext context)
        {
            if (context.IsStorage)
            {
                Stats.StorageLeafCount++;
            }
            else
            {
                Stats.AccountCount++;
            }
        }

        public void VisitCode(Keccak codeHash, VisitContext context)
        {
            Stats.CodeCount++;
        }
    }
}