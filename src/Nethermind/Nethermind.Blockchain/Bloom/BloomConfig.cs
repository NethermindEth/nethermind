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

using Nethermind.Config;
using Nethermind.Core.Attributes;

namespace Nethermind.Blockchain.Bloom
{
    public class BloomConfig : IBloomConfig
    {
        public bool Index { get; set; } = true;
        public byte[] IndexLevelBucketSizes { get; set; } = {4};
        
        [Todo("false")]
        public bool Statistics { get; set; } = true;
        
        [Todo("false")]
        public bool Migration { get; set; } = true;

        public string Directory { get; set; } = "bloom";
    }

    public interface IBloomConfig : IConfig
    {
        bool Index { get; set; }
        
        byte[] IndexLevelBucketSizes { get; set; }
        
        bool Statistics { get; set; }
        
        bool Migration { get; set; }
    }
}