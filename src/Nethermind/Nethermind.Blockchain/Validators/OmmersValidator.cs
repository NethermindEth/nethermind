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
using System.Linq;
using Nethermind.Core;

namespace Nethermind.Blockchain.Validators
{
    public class OmmersValidator : IOmmersValidator
    {
        private readonly IBlockStore _chain;
        private readonly IHeaderValidator _headerValidator;
        private readonly ILogger _logger;

        public OmmersValidator(IBlockStore chain, IHeaderValidator headerValidator, ILogger logger)
        {
            _chain = chain;
            _headerValidator = headerValidator;
            _logger = logger;
        }
        
        public bool Validate(BlockHeader header, BlockHeader[] ommers)
        {
            if (ommers.Length > 2)
            {
                _logger?.Log($"Invalid block ({header.Hash}) - too many ommers");
                return false;
            }

            if (ommers.Length == 2 && ommers[0].Hash == ommers[1].Hash)
            {
                _logger?.Log($"Invalid block ({header.Hash}) - duplicated ommer");
                return false;
            }

            foreach (BlockHeader ommer in ommers)
            {   
                if (!_headerValidator.Validate(ommer, true))
                {
                    _logger?.Log($"Invalid block ({header.Hash}) - ommer's header invalid");
                    return false;
                }
                
                if (!IsKin(header, ommer, 6))
                {
                    _logger?.Log($"Invalid block ({header.Hash}) - ommer just pretending to be ommer");
                    return false;
                }

                Block ancestor = _chain.FindBlock(header.ParentHash);
                for (int i = 0; i < 5; i++)
                {
                    if (ancestor == null)
                    {
                        break;
                    }
                    
                    if (ancestor.Ommers.Any(o => o.Hash == ommer.Hash))
                    {
                        _logger?.Log($"Invalid block ({header.Hash}) - ommers has already been included by an ancestor");
                        return false;
                    }
                    
                    ancestor = _chain.FindBlock(ancestor.Header.ParentHash);
                }
            }

            return true;
        }

        private bool IsKin(BlockHeader header, BlockHeader ommer, int relationshipLevel)
        {
            if (relationshipLevel == 0)
            {
                return false;
            }
            
            if (ommer.Number < header.Number - relationshipLevel)
            {
                return false;
            }
            
            BlockHeader parent = _chain.FindBlock(header.ParentHash)?.Header;
            if (parent == null)
            {
                return false;
            }

            if (parent.Hash == ommer.Hash)
            {
                return false;
            }

            if (parent.ParentHash == ommer.ParentHash)
            {
                return true;
            }
            
            return IsKin(parent, ommer, relationshipLevel - 1);
        }
    }
}