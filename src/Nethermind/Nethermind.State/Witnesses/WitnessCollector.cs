// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Witnesses
{
    /// <summary>
    /// <threadsafety static="true" instance="false" />
    /// </summary>
    public class WitnessCollector : IWitnessCollector, IWitnessRepository
    {
        [ThreadStatic]
        private static bool _collectWitness;

        private readonly LruCache<ValueHash256, Hash256[]> _witnessCache = new(256, "Witnesses");

        public IReadOnlyCollection<Hash256> Collected => _collected;

        public WitnessCollector([KeyFilter(DbNames.Witness)] IKeyValueStore keyValueStore, ILogger logger)
        {
            _keyValueStore = keyValueStore;
            _logger = logger;
        }

        public void Add(Hash256 hash)
        {
            if (!_collectWitness)
            {
                return;
            }
            _collected.Add(hash);
        }

        public void Reset()
        {
            _collected.Reset();
        }

        public void Persist(Hash256 blockHash)
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting {blockHash} witness ({_collected.Count})");


            if (_collected.Count > 0)
            {
                Hash256[] collected = _collected.ToArray();
                byte[] witness = new byte[collected.Length * Hash256.Size];
                Span<byte> witnessSpan = witness;

                int i = 0;
                for (var index = 0; index < collected.Length; index++)
                {
                    Hash256 keccak = collected[index];
                    keccak.Bytes.CopyTo(witnessSpan.Slice(i * Hash256.Size, Hash256.Size));
                    i++;
                }

                _keyValueStore[blockHash.Bytes] = witness;
                _witnessCache.Set(blockHash, collected);
            }
            else
            {
                _witnessCache.Set(blockHash, Array.Empty<Hash256>());
            }
        }

        class WitnessCollectorTrackingScope : IDisposable
        {
            public WitnessCollectorTrackingScope() => _collectWitness = true;
            public void Dispose() => _collectWitness = false;
        }

        public IDisposable TrackOnThisThread() => new WitnessCollectorTrackingScope();

        public Hash256[]? Load(Hash256 blockHash)
        {
            if (_witnessCache.TryGet(blockHash, out Hash256[]? witness))
            {
                if (_logger.IsTrace) _logger.Trace($"Loading cached witness for {blockHash} ({witness!.Length})");
            }
            else // not cached
            {
                byte[]? witnessData = _keyValueStore[blockHash.Bytes];
                if (witnessData is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Missing witness for {blockHash}");
                    witness = null;
                }
                else // missing from the DB
                {
                    Span<byte> witnessDataSpan = witnessData.AsSpan();
                    int itemCount = witnessData.Length / Hash256.Size;
                    if (_logger.IsTrace) _logger.Trace($"Loading non-cached witness for {blockHash} ({itemCount})");

                    Hash256[] writableWitness = new Hash256[itemCount];
                    for (int i = 0; i < itemCount; i++)
                    {
                        byte[] keccakBytes = witnessDataSpan.Slice(i * Hash256.Size, Hash256.Size).ToArray();
                        writableWitness[i] = new Hash256(keccakBytes);
                    }

                    _witnessCache.Set(blockHash, writableWitness);
                    witness = writableWitness;
                }
            }

            return witness;
        }

        public void Delete(Hash256 blockHash)
        {
            _witnessCache.Delete(blockHash);
            _keyValueStore[blockHash.Bytes] = null;
        }

        private readonly ResettableHashSet<Hash256> _collected = new();

        private readonly IKeyValueStore _keyValueStore;

        private readonly ILogger _logger;
    }
}
