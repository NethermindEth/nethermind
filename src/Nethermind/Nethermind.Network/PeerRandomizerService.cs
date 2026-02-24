// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Network
{
    /// <summary>
    /// Manages peer randomized scoring to ensure better distribution of connections.
    /// Uses a persistent random seed combined with peer ENR to generate randomized scores.
    /// </summary>
    public interface IPeerRandomizerService
    {
        /// <summary>
        /// Calculate a randomized score for a peer based on its public key.
        /// Higher scores indicate higher priority for randomized peer distribution.
        /// </summary>
        long GetRandomizedScore(PublicKey peerId);

        /// <summary>
        /// Whether randomized scoring is enabled.
        /// </summary>
        bool IsEnabled { get; }
    }

    public class PeerRandomizerService : IPeerRandomizerService
    {
        private const string RandomizedSeedKey = "PeerRandomizedSeed";
        private readonly IDb _metadataDb;
        private readonly ILogger _logger;
        private readonly bool _isEnabled;
        private byte[]? _randomizedSeed;

        public bool IsEnabled => _isEnabled;

        public PeerRandomizerService(IDb metadataDb, bool isEnabled, ILogManager logManager)
        {
            _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _isEnabled = isEnabled;

            if (_isEnabled)
            {
                InitializeRandomizedSeed();
            }
        }

        private void InitializeRandomizedSeed()
        {
            byte[] seedKey = System.Text.Encoding.UTF8.GetBytes(RandomizedSeedKey);
            byte[]? storedSeed = _metadataDb[seedKey];

            if (storedSeed is not null && storedSeed.Length == 32)
            {
                _randomizedSeed = storedSeed;
                if (_logger.IsDebug) _logger.Debug("Loaded existing peer randomized seed from metadata db");
            }
            else
            {
                _randomizedSeed = new byte[32];
                RandomNumberGenerator.Fill(_randomizedSeed);
                _metadataDb[seedKey] = _randomizedSeed;

                if (_logger.IsInfo) _logger.Info("Generated new peer randomized seed and stored in metadata db");
            }
        }

        public long GetRandomizedScore(PublicKey peerId)
        {
            if (!_isEnabled || _randomizedSeed is null)
            {
                return 0;
            }

            // Combine the randomized seed with the peer's public key to generate a deterministic score
            Span<byte> combinedData = stackalloc byte[64];
            _randomizedSeed.AsSpan().CopyTo(combinedData);
            peerId.Bytes.AsSpan(0, Math.Min(32, peerId.Bytes.Length)).CopyTo(combinedData[32..]);

            // Hash the combined data to get a randomized score
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(combinedData, hash);

            // Convert first 8 bytes to a long score (positive values only)
            long score = BitConverter.ToInt64(hash) & long.MaxValue;

            return score;
        }
    }
}
