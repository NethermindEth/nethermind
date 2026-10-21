// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;

namespace Nethermind.OpcodeTracing.Plugin.Utilities;

/// <summary>
/// Provides utility methods for directory operations.
/// </summary>
public static class DirectoryHelper
{
    /// <summary>
    /// Validates that a directory path is writable.
    /// Resolves the path using <see cref="Nethermind.Core.Extensions.PathExtensions.GetApplicationResourcePath"/>
    /// to match the actual path used by writers.
    /// </summary>
    /// <param name="path">The directory path to validate.</param>
    /// <param name="logManager">Optional log manager used to record why validation failed.</param>
    /// <returns>True if the directory is writable; otherwise, false.</returns>
    public static bool ValidateWritable(string path, ILogManager? logManager = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            // Resolve path the same way writers do to validate the actual output location
            string resolvedPath = path.GetApplicationResourcePath();

            if (!Directory.Exists(resolvedPath))
            {
                Directory.CreateDirectory(resolvedPath);
            }

            string testFile = Path.Combine(resolvedPath, $".write-test-{Guid.NewGuid()}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch (Exception ex)
        {
            if (logManager is not null)
            {
                ILogger logger = logManager.GetClassLogger(typeof(DirectoryHelper));
                if (logger.IsDebug)
                {
                    logger.Debug($"Output directory '{path}' failed write probe: {ex.Message}");
                }
            }
            return false;
        }
    }
}
