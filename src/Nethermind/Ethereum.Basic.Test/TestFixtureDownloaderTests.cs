// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class TestFixtureDownloaderTests
{
    private const string UrlTemplate = "https://fixtures.invalid/{0}/{1}";
    private const string Version = "v1.0.0";
    private const string ArchiveName = "general.tar.gz";
    private const string EntryName = "tests/fixture.txt";
    private const string EntryContent = "fixture";

    private string _cacheRoot;
    private string _suiteName;

    [SetUp]
    public void SetUp()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _suiteName = "DownloaderTests" + Guid.NewGuid().ToString("N");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, true);
        else if (File.Exists(_cacheRoot))
            File.Delete(_cacheRoot);
    }

    private static readonly TestCaseData[] TransientFailureCases =
    [
        new TestCaseData(new Func<HttpResponseMessage>[] { BadGateway, BadGateway, Archive }, 3).SetName("Bad_gateway_responses_are_retried_until_the_download_succeeds"),
        new TestCaseData(new Func<HttpResponseMessage>[] { BrokenStream, Archive }, 2).SetName("Broken_response_stream_is_retried_and_no_partial_extraction_is_used"),
        new TestCaseData(new Func<HttpResponseMessage>[] { RequestTimeout, Archive }, 2).SetName("Request_timeout_is_retried_until_the_download_succeeds"),
        new TestCaseData(new Func<HttpResponseMessage>[] { TooManyRequests, Archive }, 2).SetName("Too_many_requests_is_retried_until_the_download_succeeds"),
        new TestCaseData(new Func<HttpResponseMessage>[] { ConnectionError, Archive }, 2).SetName("Connection_error_is_retried_until_the_download_succeeds")
    ];

    [TestCaseSource(nameof(TransientFailureCases))]
    public void Transient_failures_are_retried_until_the_download_succeeds(Func<HttpResponseMessage>[] responses, int expectedRequests)
    {
        using FakeHandler handler = new(responses);

        string targetDir = Download(handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(expectedRequests));
            Assert.That(File.ReadAllText(Path.Combine(targetDir, EntryName)), Is.EqualTo(EntryContent));
            Assert.That(File.Exists(Path.Combine(targetDir, TestFixtureDownloader.MarkerFileName)), Is.True);
            AssertNoTempDirectories();
        }
    }

    [Test]
    public void Cancellation_without_a_timeout_is_not_retried()
    {
        using FakeHandler handler = new(Cancelled, Archive);

        Assert.Throws<TaskCanceledException>(() => Download(handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(1));
            AssertNoTempDirectories();
        }
    }

    [Test]
    public void Persistent_transient_failure_throws_after_max_attempts_and_leaves_no_files()
    {
        using FakeHandler handler = new(BadGateway);

        HttpRequestException exception = Assert.Throws<HttpRequestException>(() => Download(handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(TestFixtureDownloader.MaxAttempts));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(exception.Message, Does.Contain($"after {TestFixtureDownloader.MaxAttempts} attempts"));
            Assert.That(Directory.Exists(TargetDir()), Is.False);
            AssertNoTempDirectories();
        }
    }

    [Test]
    public void Non_transient_failure_is_not_retried()
    {
        using FakeHandler handler = new(NotFound);

        HttpRequestException exception = Assert.Throws<HttpRequestException>(() => Download(handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(1));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(Directory.Exists(TargetDir()), Is.False);
            AssertNoTempDirectories();
        }
    }

    [Test]
    public void Local_filesystem_failure_is_not_retried_and_keeps_its_type()
    {
        File.WriteAllText(_cacheRoot, "not a directory");
        using FakeHandler handler = new(Archive);

        IOException exception = Assert.Catch<IOException>(() => Download(handler));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(1));
            Assert.That(exception, Is.Not.InstanceOf<HttpIOException>());
        }
    }

    [Test]
    public void Completed_download_is_reused_without_a_request()
    {
        using FakeHandler first = new(Archive);
        Download(first);
        using FakeHandler handler = new(BadGateway);

        string targetDir = Download(handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Is.EqualTo(0));
            Assert.That(File.ReadAllText(Path.Combine(targetDir, EntryName)), Is.EqualTo(EntryContent));
        }
    }

    [Test]
    public void Incomplete_cache_directory_is_replaced()
    {
        Directory.CreateDirectory(TargetDir());
        File.WriteAllText(Path.Combine(TargetDir(), "partial.txt"), "partial");
        Directory.CreateDirectory(TargetDir() + TestFixtureDownloader.TempDirectoryInfix + "stale");

        using FakeHandler handler = new(Archive);

        string targetDir = Download(handler);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(Path.Combine(targetDir, "partial.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(targetDir, EntryName)), Is.EqualTo(EntryContent));
            AssertNoTempDirectories();
        }
    }

    private string Download(FakeHandler handler) =>
        TestFixtureDownloader.EnsureDownloaded(_cacheRoot, _suiteName, UrlTemplate, Version, ArchiveName, handler, TimeSpan.Zero);

    private string TargetDir() => Path.Combine(_cacheRoot, _suiteName, Version, "general");

    private void AssertNoTempDirectories()
    {
        string parentDir = Path.GetDirectoryName(TargetDir())!;
        if (!Directory.Exists(parentDir))
            return;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetDirectories(parentDir, "*" + TestFixtureDownloader.TempDirectoryInfix + "*"), Is.Empty);
        }
    }

    private static HttpResponseMessage BadGateway() => new(HttpStatusCode.BadGateway);

    private static HttpResponseMessage TooManyRequests() => new(HttpStatusCode.TooManyRequests);

    private static HttpResponseMessage ConnectionError() => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused.");

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage RequestTimeout() => throw new TaskCanceledException("The request timed out.", new TimeoutException());

    private static HttpResponseMessage Cancelled() => throw new TaskCanceledException("The request was canceled.");

    private static HttpResponseMessage Archive() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(CreateArchive()) };

    private static HttpResponseMessage BrokenStream()
    {
        byte[] archive = CreateArchive();
        return new(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(archive[..(archive.Length / 2)])) };
    }

    private static byte[] CreateArchive()
    {
        using MemoryStream output = new();
        using (GZipStream gzStream = new(output, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzStream))
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, EntryName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(EntryContent))
            };
            writer.WriteEntry(entry);
            byte[] noise = new byte[8192];
            new Random(1).NextBytes(noise);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "tests/noise.bin") { DataStream = new MemoryStream(noise) });
        }

        return output.ToArray();
    }

    private sealed class FakeHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<HttpResponseMessage> response = responses[Math.Min(Requests, responses.Length - 1)];
            Requests++;
            return response();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class FailingStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("Connection reset.");
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            return read > 0 ? read : throw new IOException("Connection reset.");
        }
    }
}
