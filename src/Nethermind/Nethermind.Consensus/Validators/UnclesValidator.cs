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

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;

namespace Nethermind.Consensus.Validators
{
    [Todo(Improve.Performance, "We execute the search up the tree twice - once for IsKin and once for HasAlreadyBeenIncluded")]
    public class UnclesValidator : IUnclesValidator
    {
        private readonly IBlockTree _blockTree;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;

        public UnclesValidator(IBlockTree? blockTree, IHeaderValidator? headerValidator, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        }
        
        public bool Validate(BlockHeader header, BlockHeader[] uncles)
        {
            if (uncles.Length > 2)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - too many uncles");
                return false;
            }

            if (uncles.Length == 2 && uncles[0].Hash == uncles[1].Hash)
            {
                _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - duplicated uncle");
                return false;
            }

            for (int i = 0; i < uncles.Length; i++)
            {
                BlockHeader uncle = uncles[i];
                if (!_headerValidator.Validate(uncle, true))
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle's header invalid");
                    return false;
                }
                
                if (!IsKin(header, uncle, 6))
                {
                    _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncle just pretending to be uncle");
                    return false;
                }

                Block ancestor = _blockTree.FindBlock(header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                for (int ancestorLevel = 0; ancestorLevel < 5; ancestorLevel++)
                {
                    if (ancestor == null)
                    {
                        break;
                    }
                    
                    if (ancestor.Uncles.Any(o => o.Hash == uncle.Hash))
                    {
                        _logger.Info($"Invalid block ({header.ToString(BlockHeader.Format.Full)}) - uncles has already been included by an ancestor");
                        return false;
                    }
                    
                    ancestor = _blockTree.FindBlock(ancestor.Header.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                }
            }

            return true;
        }

        private bool IsKin(BlockHeader header, BlockHeader uncle, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }

            if (relationshipLevel > header.Number)
            {
                return IsKin(header, uncle, (int)header.Number);
            }
            
            if (uncle.Number < header.Number - relationshipLevel)
            {
                return false;
            }
            
            BlockHeader parent = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (parent == null)
            {
                return false;
            }

            if (parent.Hash == uncle.Hash)
            {
                return false;
            }

            if (parent.ParentHash == uncle.ParentHash)
            {
                return true;
            }
            
            return IsKin(parent, uncle, relationshipLevel - 1);
        }
    }
}
