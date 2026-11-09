// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Config;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.TrustedInput;

/// <summary>The most recent trusted inputs captured, by block hash, also written to <see cref="ITrustedInputConfig.OutputDirectory"/>.</summary>
public sealed class TrustedInputStore(ITrustedInputConfig config, ILogManager logManager)
{
    private readonly LruCache<ValueHash256, byte[]> _inputs = new(ValidCacheSize(config.CacheSize), nameof(TrustedInputStore));
    private readonly ILogger _logger = logManager.GetClassLogger<TrustedInputStore>();

    public bool TryGet(Hash256 blockHash, out byte[] input) => _inputs.TryGet(blockHash, out input);

    public void Add(ulong blockNumber, Hash256 blockHash, byte[] input)
    {
        _inputs.Set(blockHash, input);

        if (config.OutputDirectory is not { } directory) return;

        string path = Path.Combine(directory, $"{blockNumber}-{blockHash.ToString(withZeroX: false)}.nti");
        // The capture and trustedinput_getInput can write the same block at once, so each writes a file of its own and
        // moves it into place whole.
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporaryPath, input);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not write the trusted input of block {blockNumber}: {ex.Message}");
            TryDelete(temporaryPath);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not delete {path}: {ex.Message}");
        }
    }

    private static int ValidCacheSize(int cacheSize) => cacheSize >= 1 ? cacheSize
        : throw new InvalidConfigurationException(
            $"TrustedInput.{nameof(ITrustedInputConfig.CacheSize)} must be at least 1 but is {cacheSize}.", ExitCodes.ForbiddenOptionValue);
}
