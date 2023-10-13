// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
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

        private readonly LruCache<ValueCommitment, Commitment[]> _witnessCache = new(256, "Witnesses");

        public IReadOnlyCollection<Commitment> Collected => _collected;

        public WitnessCollector(IKeyValueStore? keyValueStore, ILogManager? logManager)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public void Add(Commitment hash)
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

        public void Persist(Commitment blockHash)
        {
            if (_logger.IsDebug) _logger.Debug($"Persisting {blockHash} witness ({_collected.Count})");


            if (_collected.Count > 0)
            {
                Commitment[] collected = _collected.ToArray();
                byte[] witness = new byte[collected.Length * Commitment.Size];
                Span<byte> witnessSpan = witness;

                int i = 0;
                for (var index = 0; index < collected.Length; index++)
                {
                    Commitment commitment = collected[index];
                    commitment.Bytes.CopyTo(witnessSpan.Slice(i * Commitment.Size, Commitment.Size));
                    i++;
                }

                _keyValueStore[blockHash.Bytes] = witness;
                _witnessCache.Set(blockHash, collected);
            }
            else
            {
                _witnessCache.Set(blockHash, Array.Empty<Commitment>());
            }
        }

        class WitnessCollectorTrackingScope : IDisposable
        {
            public WitnessCollectorTrackingScope() => _collectWitness = true;
            public void Dispose() => _collectWitness = false;
        }

        public IDisposable TrackOnThisThread() => new WitnessCollectorTrackingScope();

        public Commitment[]? Load(Commitment blockHash)
        {
            if (_witnessCache.TryGet(blockHash, out Commitment[]? witness))
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
                    int itemCount = witnessData.Length / Commitment.Size;
                    if (_logger.IsTrace) _logger.Trace($"Loading non-cached witness for {blockHash} ({itemCount})");

                    Commitment[] writableWitness = new Commitment[itemCount];
                    for (int i = 0; i < itemCount; i++)
                    {
                        byte[] keccakBytes = witnessDataSpan.Slice(i * Commitment.Size, Commitment.Size).ToArray();
                        writableWitness[i] = new Commitment(keccakBytes);
                    }

                    _witnessCache.Set(blockHash, writableWitness);
                    witness = writableWitness;
                }
            }

            return witness;
        }

        public void Delete(Commitment blockHash)
        {
            _witnessCache.Delete(blockHash);
            _keyValueStore[blockHash.Bytes] = null;
        }

        private readonly ResettableHashSet<Commitment> _collected = new();

        private readonly IKeyValueStore _keyValueStore;

        private readonly ILogger _logger;
    }
}
