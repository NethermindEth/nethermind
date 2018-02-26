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
        private readonly IBlockHeaderValidator _headerValidator;

        public OmmersValidator(IBlockStore chain, IBlockHeaderValidator headerValidator)
        {
            _chain = chain;
            _headerValidator = headerValidator;
        }
        
        public bool Validate(BlockHeader header, BlockHeader[] ommers)
        {
            if (ommers.Length > 2)
            {
                return false;
            }

            if (ommers.Length == 2 && ommers[0].Hash == ommers[1].Hash)
            {
                return false;
            }

            foreach (BlockHeader ommer in ommers)
            {   
                if (!_headerValidator.Validate(ommer))
                {
                    return false;
                }
                
                if (!IsKin(header, ommer, 6))
                {
                    return false;
                }

                Block ancestor = _chain.FindParent(header);
                for (int i = 0; i < 5; i++)
                {
                    if (ancestor == null)
                    {
                        break;
                    }
                    
                    if (ancestor.Ommers.Any(o => o.Hash == ommer.Hash))
                    {
                        return false;
                    }
                    
                    ancestor = _chain.FindParent(ancestor.Header);
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
            
            BlockHeader parent = _chain.FindParent(header)?.Header;
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