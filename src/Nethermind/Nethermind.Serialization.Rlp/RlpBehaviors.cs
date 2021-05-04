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

namespace Nethermind.Serialization.Rlp
{
    [Flags]
    public enum RlpBehaviors
    {
        None,
        AllowExtraData = 1,
        ForSealing = 2,
        [Obsolete("Storage behaviour should be default behaviour.")]
        Storage = 4,
        Eip658Receipts = 8,
        AllowUnsigned = 16,
        SkipTypedWrapping = 32, // introduced after typed transactions. In the network (devp2p) transaction has additional wrapping
                                // when we're calculating tx hash or sending raw transaction we should skip this wrapping
                                // with additional wrapping for typed transactions we're decoding Uint8Array([TransactionType, TransactionPayload]
                                // without wrapping we're decoding (TransactionType || TransactionPayload)
        All = AllowExtraData | ForSealing | Storage | Eip658Receipts | AllowUnsigned | SkipTypedWrapping
    }
}
