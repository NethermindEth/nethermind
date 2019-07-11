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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Store
{
    public class TrieStatsCollector : ITreeVisitor
    {
        private int _lastAccountNodeCount = 0;
        
        private readonly ILogger _logger;

        public TrieStatsCollector(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }
        
        public TrieStats Stats { get; } = new TrieStats();
        
        public void VisitTree(ValueKeccak rootHash, VisitContext context)
        {
        }

        public void VisitMissingNode(ValueKeccak nodeHash, VisitContext context)
        {
            if (context.IsStorage)
            {
                Stats.MissingStorage++;
            }
            else
            {
                Stats.MissingState++;
            }
        }

        public void VisitBranch(Span<byte> hashOrRlp, VisitContext context)
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

        public void VisitExtension(Span<byte> hashOrRlp, VisitContext context)
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
        
        public void VisitLeaf(Span<byte> hashOrRlp, VisitContext context)
        {
            if (Stats.NodesCount - _lastAccountNodeCount > 100000)
            {
                _lastAccountNodeCount = Stats.NodesCount;
                _logger.Warn($"Collected info from {Stats.NodesCount} nodes. Missing CODE {Stats.MissingCode} STATE {Stats.MissingState} STORAGE {Stats.MissingStorage}");
            }
            
            if (context.IsStorage)
            {
                Stats.StorageLeafCount++;
            }
            else
            {
                Stats.AccountCount++;
            }
        }
        
        public void VisitCode(Keccak codeHash, byte[] code, VisitContext context)
        {
            if (code != null)
            {
                Stats.CodeCount++;
            }
            else
            {
                Stats.MissingCode++;
            }
        }
    }
}