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
    public static class NethermindSyncLimits
    {
        public const int MaxHeaderFetch = 512; // Amount of block headers to be fetched per retrieval request
        public const int MaxBodyFetch = 256; // Amount of block bodies to be fetched per retrieval request
        public const int MaxReceiptFetch = 256; // Amount of transaction receipts to allow fetching per request
        public const int MaxCodeFetch = 1024; // Amount of contract codes to allow fetching per request
    }
}
