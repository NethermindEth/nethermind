// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NSubstitute;
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

        Random random = new();
        Dictionary<byte[], byte[]> dict = [with(Bytes.EqualityComparer)];
        for (int i = 0; i < 1024; i++)
        {
            byte[] key = new byte[random.Next(64, 128)];
            byte[] value = new byte[random.Next(200, 250)];

            random.NextBytes(key);
            random.NextBytes(value);

            dict[key] = value;
        }

        // The writer stores an empty value as "key," and a line end
        dict[[0xee, 0xee]] = [];

        using (filePublicKeyDb.StartWriteBatch())
        {
            foreach (KeyValuePair<byte[], byte[]> kv in dict)
            {
                filePublicKeyDb[kv.Key] = kv.Value;
            }
        }

        SimpleFilePublicKeyDb copy = new("Test", Path.GetTempPath(), LimboLogs.Instance);
        Assert.That(copy.Keys.Count, Is.EqualTo(dict.Count));
        foreach (KeyValuePair<byte[], byte[]> kv in dict)
        {
            Assert.That(copy[kv.Key], Is.EqualTo(kv.Value));
        }
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void Load_skips_malformed_lines_and_keeps_the_valid_ones(string newLine)
    {
        string[] lines =
        [
            "aa,",      // empty value, as the writer emits it
            "zz",       // no comma, followed by a valid line
            "bb,01",
            "",         // blank
            "zz,01",    // non-hex key
            "bb,zz",    // non-hex value, does not overwrite bb
            "aa,bb,cc", // extra comma
            "dd,01",
            "dd,02",    // duplicate key, the last one wins
            "abc,d",    // odd-length hex
        ];
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string filePath = Path.Combine(directory.Path, SimpleFilePublicKeyDb.DbFileName);
        File.WriteAllText(filePath, string.Join(newLine, lines) + newLine + "ee,03"); // the unterminated tail is ignored
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);

        SimpleFilePublicKeyDb db = new("Test", directory.Path, new OneLoggerLogManager(new ILogger(logger)));

        Assert.That(db.GetAll().ToDictionary(static kv => kv.Key.ToHexString(), static kv => kv.Value.ToHexString()),
            Is.EquivalentTo(new Dictionary<string, string> { ["aa"] = "", ["bb"] = "01", ["dd"] = "02", ["0abc"] = "0d" }));
        string[] warnings = [.. logger.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name == nameof(InterfaceLogger.Warn))
            .Select(static call => (string)call.GetArguments()[0]!)];
        Assert.That(warnings, Is.EqualTo(new[] { 2, 4, 5, 6, 7 }
            .Select(line => $"Skipping malformed line {line} in {filePath}, expected a hex key and a hex value separated by a comma.")
            .Append("Malformed Test. Ignoring...")));
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

    [Test]
    public void Get_returns_null_for_missing_key()
    {
        using TempPath tempPath = TempPath.GetTempFile(SimpleFilePublicKeyDb.DbFileName);
        tempPath.Dispose();

        SimpleFilePublicKeyDb filePublicKeyDb = new("Test", Path.GetTempPath(), LimboLogs.Instance);

        byte[]? result = filePublicKeyDb.Get([1, 2, 3]);

        Assert.That(result, Is.Null);
    }
}
