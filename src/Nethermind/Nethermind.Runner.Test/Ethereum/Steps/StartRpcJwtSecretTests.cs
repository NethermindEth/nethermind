// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.Versioning;
using Nethermind.JsonRpc;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Steps;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

public class StartRpcJwtSecretTests
{
    [Test, NonParallelizable, Platform(Exclude = "Win")]
    [UnsupportedOSPlatform("windows")]
    public void ConfigureJwtSecret_keeps_the_working_directory_secret_when_only_the_owner_can_write()
    {
        string keyStoreDirectory = $"keystore-{Guid.NewGuid():N}";
        string previousWorkingDirectory = Directory.GetCurrentDirectory();
        DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory();
        try
        {
            string workingDirectorySecret = Path.Join(workingDirectory.FullName, keyStoreDirectory, "jwt-secret");
            Directory.CreateDirectory(Path.GetDirectoryName(workingDirectorySecret)!);
            File.WriteAllText(workingDirectorySecret, "secret");

            File.SetUnixFileMode(workingDirectory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.SetCurrentDirectory(workingDirectory.FullName);
            JsonRpcConfig jsonRpcConfig = new();

            StartRpc.ConfigureJwtSecret(new KeyStoreConfig { KeyStoreDirectory = keyStoreDirectory }, jsonRpcConfig, LimboLogs.Instance.GetClassLogger<StartRpcJwtSecretTests>());

            Assert.That(jsonRpcConfig.JwtSecretFile, Is.EqualTo(Path.Join(Directory.GetCurrentDirectory(), keyStoreDirectory, "jwt-secret")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousWorkingDirectory);
            workingDirectory.Delete(recursive: true);
        }
    }

    [Test, NonParallelizable, Platform(Exclude = "Win")]
    [UnsupportedOSPlatform("windows")]
    public void ConfigureJwtSecret_does_not_read_a_secret_from_a_working_directory_others_can_write([Values("", "./")] string prefix, [Values] bool groupWritable)
    {
        string keyStoreName = $"keystore-{Guid.NewGuid():N}";
        string keyStoreDirectory = prefix + keyStoreName;
        string appSecretPath = Path.GetFullPath(Path.Join(PathUtils.ExecutingDirectory, keyStoreName, "jwt-secret"));
        string previousWorkingDirectory = Directory.GetCurrentDirectory();
        DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory();
        try
        {
            string workingDirectorySecret = Path.Join(workingDirectory.FullName, keyStoreName, "jwt-secret");
            string legacySecret = Path.Join(workingDirectory.FullName, "keystore", "jwt-secret");
            foreach (string secret in new[] { workingDirectorySecret, legacySecret })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
                File.WriteAllText(secret, "secret");
            }

            File.SetUnixFileMode(workingDirectory.FullName, groupWritable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            Directory.SetCurrentDirectory(workingDirectory.FullName);
            JsonRpcConfig jsonRpcConfig = new();

            StartRpc.ConfigureJwtSecret(new KeyStoreConfig { KeyStoreDirectory = keyStoreDirectory }, jsonRpcConfig, LimboLogs.Instance.GetClassLogger<StartRpcJwtSecretTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(jsonRpcConfig.JwtSecretFile, Is.EqualTo(appSecretPath));
                Assert.That(File.Exists(legacySecret), Is.True);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(previousWorkingDirectory);
            workingDirectory.Delete(recursive: true);
        }
    }
}
