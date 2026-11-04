// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Wallet.Test;

public class ProtectedPrivateKeyTests
{
    // Changes the working directory, so a relative keystore path resolving against it would be caught.
    [Test, NonParallelizable]
    public void Creates_keys_in_keyStoreDirectory()
    {
        // Don't test on Windows, as DpapiWrapper used
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        string keyStoreDir = Path.Combine("testKeyStoreDir", Path.GetRandomFileName());
        string workingDirectory = Directory.GetCurrentDirectory();
        DirectoryInfo otherDirectory = Directory.CreateTempSubdirectory();

        try
        {
            PrivateKey unprotected;
            Directory.SetCurrentDirectory(otherDirectory.FullName);
            try
            {
                // Both Protect and Unprotect run while the working directory is switched.
                ProtectedPrivateKey key = new(TestItem.PrivateKeyA, keyStoreDir);
                unprotected = key.Unprotect();
            }
            finally
            {
                Directory.SetCurrentDirectory(workingDirectory);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Directory.EnumerateFiles(Path.Combine(keyStoreDir.GetApplicationResourcePath(), "protection_keys")).Count(), Is.EqualTo(1));
                Assert.That(otherDirectory.EnumerateFileSystemInfos(), Is.Empty);

                Assert.That(unprotected, Is.EqualTo(TestItem.PrivateKeyA));
            }
        }
        finally
        {
            otherDirectory.Delete(recursive: true);
            string resolvedKeyStoreDir = keyStoreDir.GetApplicationResourcePath();
            if (Directory.Exists(resolvedKeyStoreDir))
            {
                Directory.Delete(resolvedKeyStoreDir, recursive: true);
            }
        }
    }
}
