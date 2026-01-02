// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Wallet.Test;

public class ProtectedPrivateKeyTests
{
    [Test]
    public void Creates_keys_in_keyStoreDirectory()
    {
        // Don't test on Windows, as DpapiWrapper used
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        string keyStoreDir = Path.Combine("testKeyStoreDir", Path.GetRandomFileName());

        ProtectedPrivateKey key = new(TestItem.PrivateKeyA, keyStoreDir);

        Directory.EnumerateFiles(Path.Combine(keyStoreDir, "protection_keys")).Count().Should().Be(1);

        key.Unprotect().Should().Be(TestItem.PrivateKeyA);
    }
}
