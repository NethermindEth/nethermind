// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Witnesses
{
    /// <summary>
    /// <threadsafety static="true" instance="false" />
    /// </summary>
    public class WitnessCollector : IWitnessCollector, IWitnessRepository
    {
        [ThreadStatic]
        private static bool _collectWitness;

        private readonly LruCache<ValueKeccak, Keccak[]> _witnessCache = new(256, "Witnesses");

        public IReadOnlyCollection<Keccak> Collected => _collected;

        public WitnessCollector(IKeyValueStore? keyValueStore, ILogManager? logManager)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Add(Keccak hash)
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

        public void Persist(Keccak blockHash)
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting {blockHash} witness ({_collected.Count})");


            if (_collected.Count > 0)
            {
                Keccak[] collected = _collected.ToArray();
                byte[] witness = new byte[collected.Length * Keccak.Size];
                Span<byte> witnessSpan = witness;

                int i = 0;
                for (var index = 0; index < collected.Length; index++)
                {
                    Keccak keccak = collected[index];
                    keccak.ValueKeccak.Bytes.CopyTo(witnessSpan.Slice(i * Keccak.Size, Keccak.Size));
                    i++;
                }

                _keyValueStore[blockHash.ValueKeccak.Bytes] = witness;
                _witnessCache.Set(in blockHash.ValueKeccak, collected);
            }
            else
            {
                _witnessCache.Set(in blockHash.ValueKeccak, Array.Empty<Keccak>());
            }
        }

        class WitnessCollectorTrackingScope : IDisposable
        {
            public WitnessCollectorTrackingScope() => _collectWitness = true;
            public void Dispose() => _collectWitness = false;
        }

        public IDisposable TrackOnThisThread() => new WitnessCollectorTrackingScope();

        public Keccak[]? Load(Keccak blockHash)
        {
            if (_witnessCache.TryGet(in blockHash.ValueKeccak, out Keccak[]? witness))
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
                    int itemCount = witnessData.Length / Keccak.Size;
                    if (_logger.IsTrace) _logger.Trace($"Loading non-cached witness for {blockHash} ({itemCount})");

                    Keccak[] writableWitness = new Keccak[itemCount];
                    for (int i = 0; i < itemCount; i++)
                    {
                        byte[] keccakBytes = witnessDataSpan.Slice(i * Keccak.Size, Keccak.Size).ToArray();
                        writableWitness[i] = new Keccak(keccakBytes);
                    }

                    _witnessCache.Set(in blockHash.ValueKeccak, writableWitness);
                    witness = writableWitness;
                }
            }

            return witness;
        }

        public void Delete(Keccak blockHash)
        {
            _witnessCache.Delete(in blockHash.ValueKeccak);
            _keyValueStore[blockHash.Bytes] = null;
        }

        private readonly ResettableHashSet<Keccak> _collected = new();

        private readonly IKeyValueStore _keyValueStore;

        private readonly ILogger _logger;
    }
}
