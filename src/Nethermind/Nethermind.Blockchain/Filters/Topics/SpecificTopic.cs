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

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Filters.Topics
{
    public class SpecificTopic : TopicExpression
    {
        private readonly Keccak _topic;
        private Core.Bloom.BloomExtract? _bloomExtract;

        public SpecificTopic(Keccak topic)
        {
            _topic = topic;
        }
        
        private Core.Bloom.BloomExtract BloomExtract => _bloomExtract ??= Core.Bloom.GetExtract(_topic);

        public override bool Accepts(Keccak topic) => topic == _topic;
        
        public override bool Accepts(ref KeccakStructRef topic) => topic == _topic;

        public override bool Matches(Bloom bloom) => bloom.Matches(BloomExtract);
        
        public override bool Matches(ref BloomStructRef bloom) => bloom.Matches(BloomExtract);
    }
}
