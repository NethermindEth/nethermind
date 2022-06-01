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

namespace Nethermind.Merge.Plugin.Data.V1
{
    public static class PayloadStatus
    {
        /// <summary>
        /// Payload is valid.
        /// </summary>
        public const string Valid = "VALID";

        /// <summary>
        /// Payload is invalid.
        /// </summary>
        public const string Invalid = "INVALID";

        /// <summary>
        /// Payload started a sync.
        /// </summary>
        public const string Syncing = "SYNCING";
        
        /// <summary>
        /// Payload was accepted but not executed yet. It can be executed in <see cref="ForkchoiceStateV1"/> call.
        /// </summary>
        public const string Accepted = "ACCEPTED";
        
        /// <summary>
        /// Payload has invalid block hash.
        /// </summary>
        public const string InvalidBlockHash = "INVALID_BLOCK_HASH";
    }
}
