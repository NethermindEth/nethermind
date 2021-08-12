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

using Nethermind.Config;

namespace Nethermind.TxPool
{
    public interface ITxPoolConfig : IConfig
    {
        [ConfigItem(DefaultValue = "5", Description = "Defines percentage of peers receiving the tx gossips.")]
        int PeerNotificationThreshold { get; set; }
        
        [ConfigItem(DefaultValue = "2048", Description = "Max number of transactions held in mempool (more transactions in mempool mean more memory used")]
        int Size { get; set; }
        
        [ConfigItem(DefaultValue = "256", Description = "Defines how much into the future transactions are kept.")]
        uint FutureNonceRetention { get; set; }
        
        [ConfigItem(DefaultValue = "524288",
            Description = "Max number of cached hashes of already known transactions." +
                          "It is set automatically by the memory hint.")]
        int HashCacheSize { get; set; }
        
        [ConfigItem(DefaultValue = "null",
            Description = "Max transaction gas allowed.")]
        long? GasLimit { get; set; }        
    }
}
