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

namespace Nethermind.Blockchain
{
    public static class HealthHintConstants
    {
        public const int ProcessingSafetyMultiplier = 4;

        public static ulong? InfinityHint = null;
        
        public const int EthashStandardProcessingPeriod = 15;
        
        public const int EthashProcessingSafetyMultiplier = 12;

        public const int ProducingSafetyMultiplier = 2;
    }
    
    public interface IHealthHintService
    {
        /// <summary>
        /// Get processing time assumption based on the network.
        /// </summary>
        /// <returns><value>null</value> if we cannot assume processing interval, otherwise returns the number of seconds for maximum time without processed block</returns>
        ulong? MaxSecondsIntervalForProcessingBlocksHint();
        
        /// <summary>
        /// Get producing time assumption based on the network.
        /// </summary>
        /// <returns><value>null</value> if we cannot assume producing interval, otherwise returns the number of seconds for maximum time without produced block</returns>
        ulong? MaxSecondsIntervalForProducingBlocksHint();
    }
}
