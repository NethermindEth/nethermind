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

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    public class NullWitnessCollector : IWitnessCollector, IWitnessRepository
    {
        private NullWitnessCollector() { }

        public static NullWitnessCollector Instance { get; } = new();
        
        public IReadOnlyCollection<Keccak> Collected => Array.Empty<Keccak>();

        public void Add(Keccak hash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Add)} calls.");
        }

        public void Reset() { }
        
        public void Persist(Keccak blockHash) { }

        public Keccak[]? Load(Keccak blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Load)} calls.");
        }

        public void Delete(Keccak blockHash)
        {
            throw new InvalidOperationException(
                $"{nameof(NullWitnessCollector)} is not expected to receive {nameof(Delete)} calls.");
        }
    }
}
