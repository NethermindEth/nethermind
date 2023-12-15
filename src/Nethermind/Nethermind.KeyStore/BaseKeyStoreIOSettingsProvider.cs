// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Logging;

namespace Nethermind.KeyStore
{
    public class BaseKeyStoreIOSettingsProvider
    {
        public static string GetStoreDirectory(string keyStoreFolderName)
        {
            // TODO - we should have a file system implementation that does this
            var directory = keyStoreFolderName.GetApplicationResourcePath();
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return directory;
        }
    }
}
