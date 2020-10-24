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
using System.Linq;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Witnesses
{
    /// <summary>
    /// <threadsafety static="true" instance="false" />
    /// </summary>
    public class WitnessCollector : IWitnessCollector
    {
        private LruCache<Keccak, IReadOnlyList<Keccak>> _witnessCache
            = new LruCache<Keccak, IReadOnlyList<Keccak>>(256, "Witnesses");
        
        public IReadOnlyCollection<Keccak> Collected => _collected;

        public WitnessCollector(IKeyValueStore? keyValueStore, ILogManager? logManager)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
            if(_logger.IsDebug) _logger.Debug($"Persisting witness for {blockHash}");

            if (_collected.Count > 0)
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
                _witnessCache.Set(blockHash, _collected.ToArray());
            }
            else
            {
                _witnessCache.Set(blockHash, Array.Empty<Keccak>());
            }
        }

        public IReadOnlyCollection<Keccak>? Load(Keccak blockHash)
        {
            IReadOnlyList<Keccak> witness;
            if (_witnessCache.Contains(blockHash))
            {
                if(_logger.IsTrace) _logger.Trace($"Loading cached witness for {blockHash}");
                witness = _witnessCache.Get(blockHash);
            }
            else // not cached
            {
                byte[] witnessData = _keyValueStore[blockHash.Bytes];
                if (witnessData == null)
                {
                    if(_logger.IsTrace) _logger.Trace($"Missing witness for {blockHash}");
                    witness = null;
                }
                else // missing from the DB
                {
                    if(_logger.IsTrace) _logger.Trace($"Loading non-cached witness for {blockHash}");
                    Span<byte> witnessDataSpan = witnessData.AsSpan();
                    int itemCount = witnessData.Length / Keccak.Size;
                    Keccak[] writableWitness = new Keccak[itemCount];
                    for (int i = 0; i < itemCount; i++)
                    {
                        byte[] keccakBytes = witnessDataSpan.Slice(i * Keccak.Size, Keccak.Size).ToArray();
                        writableWitness[i] = new Keccak(keccakBytes);
                    }
                
                    _witnessCache.Set(blockHash, writableWitness);
                    witness = writableWitness;   
                }
            }

            return witness;
        }

        private HashSet<Keccak> _collected = new HashSet<Keccak>();

        private readonly IKeyValueStore _keyValueStore;
        
        private readonly ILogger _logger;
    }
}