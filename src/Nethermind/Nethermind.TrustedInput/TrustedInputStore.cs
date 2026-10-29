// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.TrustedInput;

/// <summary>The most recent trusted inputs captured, by block hash, also written to <see cref="ITrustedInputConfig.OutputDirectory"/>.</summary>
public sealed class TrustedInputStore(ITrustedInputConfig config, ILogManager logManager)
{
    private readonly LruCache<ValueHash256, byte[]> _inputs = new(config.CacheSize, nameof(TrustedInputStore));
    private readonly ILogger _logger = logManager.GetClassLogger<TrustedInputStore>();

    public bool TryGet(Hash256 blockHash, out byte[] input) => _inputs.TryGet(blockHash, out input);

    public void Add(ulong blockNumber, Hash256 blockHash, byte[] input)
    {
        _inputs.Set(blockHash, input);

        if (config.OutputDirectory is not { } directory) return;

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"{blockNumber}-{blockHash.ToString(withZeroX: false)}.nti"), input);
        }
        catch (IOException ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not write the trusted input of block {blockNumber}: {ex.Message}");
        }
    }
}
