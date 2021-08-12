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

namespace Nethermind.TxPool
{
    [Flags]
    public enum TxHandlingOptions
    {
        None = 0,
        
        /// <summary>
        /// Tries to find the valid nonce for the given account
        /// </summary>
        ManagedNonce = 1,
        
        /// <summary>
        /// Keeps trying to push the transaction until it is included in a block
        /// </summary>
        PersistentBroadcast = 2,
        
        /// <summary>
        /// old style signature without replay attack protection (before the ETC and ETH split)
        /// </summary>
        PreEip155Signing = 4,
        
        /// <summary>
        /// Allows transaction to be signed by node even if its already signed
        /// </summary>
        AllowReplacingSignature = 8,

        All = ManagedNonce | PersistentBroadcast | PreEip155Signing | AllowReplacingSignature
    }
}
