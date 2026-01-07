// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin.Utilities;

/// <summary>
/// Provides utility methods for directory operations.
/// </summary>
public static class DirectoryHelper
{
    /// <summary>
    /// Validates that a directory path is writable.
    /// </summary>
    /// <param name="path">The directory path to validate.</param>
    /// <returns>True if the directory is writable; otherwise, false.</returns>
    public static bool ValidateWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            string testFile = Path.Combine(path, $".write-test-{Guid.NewGuid()}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
