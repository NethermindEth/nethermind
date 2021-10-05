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

namespace Nethermind.Blockchain
{
    [Flags]
    public enum SuggestingOptions
    {
        None = 0,
        
        /// <summary>
        /// Whether a block should be processed or just added to the store.
        /// </summary>
        ShouldProcess = 1,
        
        /// <summary>
        /// Whether a block should be set as main.
        /// </summary>
        SetAsMain = 2,
        
        /// <summary>
        /// Whether a block shouldn't be set as main.
        /// </summary>
        DontSetAsMain = 4,
        
        /// <summary>
        /// Whether a proof of stake is enabled.
        /// </summary>
        PoSEnabled = 8,
    }
}
