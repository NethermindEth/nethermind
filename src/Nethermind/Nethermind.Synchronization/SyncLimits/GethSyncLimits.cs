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

namespace Nethermind.Synchronization.SyncLimits
{
    public static class GethSyncLimits
    {
        public const int MaxHeaderFetch = 192; // Amount of block headers to be fetched per retrieval request
        
        // change MaxBodyFetch to 128
        public const int MaxBodyFetch = 128; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 128; // Amount of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 64; // Amount of contract codes to allow fetching per request
        public const int MaxProofsFetch = 64; // Amount of merkle proofs to be fetched per retrieval request
        public const int MaxHelperTrieProofsFetch = 64; // Amount of helper tries to be fetched per retrieval request
        public const int MaxTxSend = 64; // Amount of transactions to be send per request
        public const int MaxTxStatus = 256; // Amount of transactions to queried per request   
    }
}
