// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Init.Snapshot.Test;

[TestFixture]
public class SnapshotDownloaderTests
{
    private FlakySnapshotServer _server = null!;
    private TempPath _tempDir = null!;
    private string _destinationPath = null!;

    [SetUp]
    public void SetUp()
    {
        _server = new FlakySnapshotServer();
        _tempDir = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_tempDir.Path);
        _destinationPath = Path.Combine(_tempDir.Path, "snapshot.tar.zst");
    }

    [TearDown]
    public void TearDown()
    {
        _server.Dispose();
        _tempDir.Dispose();
    }

    [Test]
    public async Task DownloadAsync_NoFileOnDisk_DownloadsExactContent()
    {
        byte[] content = BuildContent();
        _server.Content = content;
        using SnapshotDownloader downloader = new(LimboLogs.Instance);

        await downloader.DownloadAsync(_server.Url, _destinationPath, CancellationToken.None);

        Assert.That(File.ReadAllBytes(_destinationPath), Is.EqualTo(content),
            "a fresh download must write the exact remote bytes");
    }

    [Test]
    public async Task DownloadAsync_PartialFileOnDisk_ResumesWithoutCorruption()
    {
        byte[] content = BuildContent();
        _server.Content = content;
        File.WriteAllBytes(_destinationPath, content[..30_000]);
        using SnapshotDownloader downloader = new(LimboLogs.Instance);

        await downloader.DownloadAsync(_server.Url, _destinationPath, CancellationToken.None);

        Assert.That(File.ReadAllBytes(_destinationPath), Is.EqualTo(content),
            "a resumed download must append exactly the missing suffix, not replay the full body");
    }

    [Test]
    public async Task DownloadAsync_FileAlreadyComplete_LeavesItUntouched()
    {
        byte[] content = BuildContent();
        _server.Content = content;
        File.WriteAllBytes(_destinationPath, content);
        using SnapshotDownloader downloader = new(LimboLogs.Instance);

        await downloader.DownloadAsync(_server.Url, _destinationPath, CancellationToken.None);

        Assert.That(File.ReadAllBytes(_destinationPath), Is.EqualTo(content),
            "a complete file must be recognized via 416 and left as is");
    }

    [Test]
    public async Task DownloadAsync_ServerRedirects_FollowsAndDownloads()
    {
        byte[] content = BuildContent();
        _server.Content = content;
        _server.RedirectFirstRequest = true;
        using SnapshotDownloader downloader = new(LimboLogs.Instance);

        await downloader.DownloadAsync(_server.Url, _destinationPath, CancellationToken.None);

        Assert.That(File.ReadAllBytes(_destinationPath), Is.EqualTo(content),
            "a redirect must be followed manually so the range header survives");
    }

    private static byte[] BuildContent()
    {
        byte[] content = new byte[100_000];
        new Random(42).NextBytes(content);
        return content;
    }
}
