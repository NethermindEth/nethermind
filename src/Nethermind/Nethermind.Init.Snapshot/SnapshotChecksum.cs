// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Init.Snapshot;

internal static class SnapshotChecksum
{
    public static bool Verify(byte[] actual, string expectedHex, string onMismatch, ILogger logger)
    {
        byte[] expected = Bytes.FromHexString(expectedHex);
        if (Bytes.AreEqual(actual, expected))
        {
            if (logger.IsInfo)
                logger.Info("Snapshot checksum verified.");
            return true;
        }

        if (logger.IsError)
            logger.Error(
                $"Snapshot checksum verification failed. Expected: {expectedHex}, actual: {Convert.ToHexString(actual).ToLowerInvariant()}. {onMismatch}");
        return false;
    }
}
