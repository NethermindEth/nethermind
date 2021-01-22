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
// 

using System;
using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface ISealValidator
    {
        bool ValidateParams(BlockHeader parent, BlockHeader header);
        
        /// <summary>
        /// Validates block header seal.
        /// </summary>
        /// <param name="header">Block header to validate.</param>
        /// <param name="force">Unless set to <value>true</value> the validator is allowed to optimize validation away in a safe manner.</param>
        /// <returns><value>True</value> if seal is valid or was not checked, otherwise <value>false</value></returns>
        bool ValidateSeal(BlockHeader header, bool force);
        
        public void HintValidationRange(Guid guid, long start, long end) { }
    }
}
