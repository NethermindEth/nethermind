// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Api;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatLayoutResolverTests
{
    [TestCase(FlatLayout.Auto, true, null, false, false, 8, FlatLayout.FlatInTrie, "FlatInTrie", TestName = "New DB, low memory")]
    [TestCase(FlatLayout.Auto, true, null, false, false, 32, FlatLayout.Flat, "Flat", TestName = "New DB, ample memory")]
    [TestCase(FlatLayout.Auto, true, null, false, true, 8, FlatLayout.Flat, "Flat", TestName = "New DB, history enabled")]
    [TestCase(FlatLayout.Auto, true, null, true, false, 8, FlatLayout.Flat, "Flat", TestName = "DB created before Auto")]
    [TestCase(FlatLayout.Auto, true, "FlatInTrie", true, false, 32, FlatLayout.FlatInTrie, "FlatInTrie", TestName = "Recorded FlatInTrie, memory added")]
    [TestCase(FlatLayout.Auto, true, "Flat", true, false, 8, FlatLayout.Flat, "Flat", TestName = "Recorded Flat, low memory")]
    [TestCase(FlatLayout.Flat, true, null, false, false, 8, FlatLayout.Flat, null, TestName = "Explicit Flat")]
    [TestCase(FlatLayout.FlatInTrie, true, null, false, false, 32, FlatLayout.FlatInTrie, null, TestName = "Explicit FlatInTrie")]
    [TestCase(FlatLayout.Auto, false, null, false, false, 8, FlatLayout.Flat, null, TestName = "Flat DB disabled")]
    public void Resolve(FlatLayout configured, bool enabled, string recorded, bool hasSstFile, bool historyEnabled, long memoryGiB,
        FlatLayout expected, string expectedRecorded)
    {
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        string markerPath = CreateFlatDirectory(baseDbPath, recorded, hasSstFile);
        FlatDbConfig config = new() { Layout = configured, Enabled = enabled, HistoryEnabled = historyEnabled };

        Resolve(config, baseDbPath, memoryGiB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Layout, Is.EqualTo(expected));
            Assert.That(File.Exists(markerPath) ? File.ReadAllText(markerPath) : null, Is.EqualTo(expectedRecorded));
        }
    }

    [Test]
    public void Resolve_refuses_an_unrecognized_recorded_layout([Values("Auto", "PreimageFlat", "7", "garbage")] string recorded)
    {
        using TempPath baseDbPath = TempPath.GetTempDirectory();
        CreateFlatDirectory(baseDbPath, recorded, hasSstFile: true);

        Assert.Throws<InvalidConfigurationException>(() => Resolve(new FlatDbConfig(), baseDbPath, 32));
    }

    private static string CreateFlatDirectory(TempPath baseDbPath, string recorded, bool hasSstFile)
    {
        string flatPath = Directory.CreateDirectory(DbNames.Flat.GetApplicationResourcePath(baseDbPath.Path)).FullName;
        string markerPath = Path.Combine(flatPath, FlatLayoutResolver.MarkerFileName);
        if (recorded is not null) File.WriteAllText(markerPath, recorded);
        if (hasSstFile) File.WriteAllText(Path.Combine(flatPath, "000001.sst"), "");
        return markerPath;
    }

    private static void Resolve(FlatDbConfig config, TempPath baseDbPath, long memoryGiB) =>
        new FlatLayoutResolver(new InitConfig { BaseDbPath = baseDbPath.Path }, new TestHardwareInfo(memoryGiB.GiB), new RealFileSystem(), LimboLogs.Instance)
            .Resolve(config);
}
