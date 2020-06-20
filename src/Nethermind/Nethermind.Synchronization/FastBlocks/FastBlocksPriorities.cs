//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Synchronization.FastBlocks
{
    internal static class FastBlocksPriorities
    {
        /// <summary>
        /// Batches that are so close to the lowest inserted header will be prioritized
        /// </summary>
        // public const long ForHeaders = 16 * 1024;
        public const long ForHeaders = FastBlocksQueueLimits.ForHeaders;

        /// <summary>
        /// Batches that are so close to the lowest inserted body will be prioritized
        /// </summary>
        // public const long ForBodies = 2 * 1024;
        public const long ForBodies = FastBlocksQueueLimits.ForBodies;

        /// <summary>
        /// Batches that are so close to the lowest inserted receipt will be prioritized
        /// </summary>
        // public const long ForReceipts = 2 * 1024;
        public const long ForReceipts = FastBlocksQueueLimits.ForReceipts;
    }
}