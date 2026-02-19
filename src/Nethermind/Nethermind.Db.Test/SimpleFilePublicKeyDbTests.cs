// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.Self)]
public class SimpleFilePublicKeyDbTests
{
    [Test]
    public void Save_and_load()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);

        Random random = new Random();
        Dictionary<byte[], byte[]> dict = new Dictionary<byte[], byte[]>(Bytes.EqualityComparer);
        for (int i = 0; i < 1024; i++)
        {
            byte[] key = new byte[random.Next(64, 128)];
            byte[] value = new byte[random.Next(200, 250)];

            random.NextBytes(key);
            random.NextBytes(value);

            dict[key] = value;
        }

        using (filePublicKeyDb.StartWriteBatch())
        {
            foreach (var kv in dict)
            {
                filePublicKeyDb[kv.Key] = kv.Value;
            }
        }

        SimpleFilePublicKeyDb copy = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        Assert.That(copy.Keys.Count, Is.EqualTo(dict.Count));
        foreach (var kv in dict)
        {
            Assert.That(filePublicKeyDb[kv.Key].AsSpan().SequenceEqual(kv.Value), Is.True);
        }
    }

    [Test]
    public void Set_updates_existing_key_with_different_value()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);

        byte[] key = [1, 2, 3];
        byte[] originalValue = [10, 20, 30];
        byte[] updatedValue = [40, 50, 60];

        filePublicKeyDb[key] = originalValue;
        filePublicKeyDb[key] = updatedValue;

        Assert.That(filePublicKeyDb[key], Is.EqualTo(updatedValue));
    }

    [Test]
    public void Set_with_identical_value_does_not_trigger_persistence()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        byte[] key = [1, 2, 3];
        byte[] originalValue = [10, 20, 30];

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        filePublicKeyDb[key] = originalValue;
        using (filePublicKeyDb.StartWriteBatch()) { }

        // Delete the file to detect if a second flush writes anything
        File.Delete(Path.Combine(Path.GetTempPath(), SimpleFilePublicKeyDb.DbFileName));

        // Set the same value again — should not mark pending changes
        filePublicKeyDb[key] = [10, 20, 30];
        using (filePublicKeyDb.StartWriteBatch()) { }

        // File should not be recreated since there were no pending changes
        Assert.That(File.Exists(Path.Combine(Path.GetTempPath(), SimpleFilePublicKeyDb.DbFileName)), Is.False);
    }

    [Test]
    public void Set_persists_updated_value_after_reload()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        byte[] key = [1, 2, 3];
        byte[] originalValue = [10, 20, 30];
        byte[] updatedValue = [40, 50, 60];

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        filePublicKeyDb[key] = originalValue;
        using (filePublicKeyDb.StartWriteBatch()) { }

        filePublicKeyDb[key] = updatedValue;
        using (filePublicKeyDb.StartWriteBatch()) { }

        SimpleFilePublicKeyDb reloaded = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        Assert.That(reloaded[key], Is.EqualTo(updatedValue));
    }

    [Test]
    public void Clear()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        using (filePublicKeyDb.StartWriteBatch())
        {
            filePublicKeyDb[[1, 2, 3]] = [1, 2, 3];
        }

        Assert.That(filePublicKeyDb.KeyExists([1, 2, 3]), Is.True);
        filePublicKeyDb.Clear();
        Assert.That(filePublicKeyDb.KeyExists([1, 2, 3]), Is.False);
    }
}
