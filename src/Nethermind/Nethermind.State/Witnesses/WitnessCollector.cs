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

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.State.Witnesses
{
    /// <summary>
    /// <threadsafety static="true" instance="false" />
    /// </summary>
    public class WitnessCollector : IWitnessCollector
    {
        public IEnumerable<Keccak> Collected => _collected;

        public WitnessCollector(IKeyValueStore keyValueStore)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
        }

        public void Add(Keccak hash)
        {
            _collected.Add(hash);
        }

        public void Reset()
        {
            _collected = new HashSet<Keccak>();
        }

        public void Persist(Keccak blockHash)
        {
            byte[] witness = new byte[_collected.Count * Keccak.Size];
            Span<byte> witnessSpan = witness;

            int i = 0;
            foreach (Keccak keccak in _collected)
            {
                keccak.Bytes.AsSpan().CopyTo(witnessSpan.Slice(i * Keccak.Size, Keccak.Size));
                i++;
            }

            _keyValueStore[blockHash.Bytes] = witness;
        }

        public Keccak[] Load(Keccak blockHash)
        {
            byte[] witnessData = _keyValueStore[blockHash.Bytes];
            Span<byte> witnessDataSpan = witnessData.AsSpan();
            
            int itemCount = witnessData.Length / Keccak.Size;
            Keccak[] witness = new Keccak[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                byte[] keccakBytes = witnessDataSpan.Slice(i * Keccak.Size, Keccak.Size).ToArray();
                witness[i] = new Keccak(keccakBytes);
            }

            return witness;
        }

        private HashSet<Keccak> _collected = new HashSet<Keccak>();

        private readonly IKeyValueStore _keyValueStore;
    }
}