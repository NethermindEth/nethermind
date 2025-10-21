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
    /// Manages peer diversity scoring to ensure better distribution of connections.
    /// Uses a persistent random seed combined with peer ENR to generate diversity scores.
    /// </summary>
    public interface IPeerDiversityService
    {
        /// <summary>
        /// Calculate a diversity score for a peer based on its public key.
        /// Higher scores indicate higher priority for diverse peer distribution.
        /// </summary>
        long GetDiversityScore(PublicKey peerId);

        /// <summary>
        /// Whether diversity scoring is enabled.
        /// </summary>
        bool IsEnabled { get; }
    }

    public class PeerDiversityService : IPeerDiversityService
    {
        private const string DiversitySeedKey = "PeerDiversitySeed";
        private readonly IDb _metadataDb;
        private readonly ILogger _logger;
        private readonly bool _isEnabled;
        private byte[]? _diversitySeed;

        public bool IsEnabled => _isEnabled;

        public PeerDiversityService(IDb metadataDb, bool isEnabled, ILogManager logManager)
        {
            _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _isEnabled = isEnabled;

            if (_isEnabled)
            {
                InitializeDiversitySeed();
            }
        }

        private void InitializeDiversitySeed()
        {
            byte[] seedKey = System.Text.Encoding.UTF8.GetBytes(DiversitySeedKey);
            byte[]? storedSeed = _metadataDb[seedKey];

            if (storedSeed is not null && storedSeed.Length == 32)
            {
                _diversitySeed = storedSeed;
                if (_logger.IsDebug) _logger.Debug("Loaded existing peer diversity seed from metadata db");
            }
            else
            {
                _diversitySeed = new byte[32];
                RandomNumberGenerator.Fill(_diversitySeed);
                _metadataDb[seedKey] = _diversitySeed;

                if (_logger.IsInfo) _logger.Info("Generated new peer diversity seed and stored in metadata db");
            }
        }

        public long GetDiversityScore(PublicKey peerId)
        {
            if (!_isEnabled || _diversitySeed is null)
            {
                return 0;
            }

            // Combine the diversity seed with the peer's public key to generate a deterministic score
            Span<byte> combinedData = stackalloc byte[64];
            _diversitySeed.AsSpan().CopyTo(combinedData);
            peerId.Bytes.AsSpan(0, Math.Min(32, peerId.Bytes.Length)).CopyTo(combinedData[32..]);

            // Hash the combined data to get a diversity score
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(combinedData, hash);

            // Convert first 8 bytes to a long score (positive values only)
            long score = BitConverter.ToInt64(hash) & long.MaxValue;

            return score;
        }
    }
}
