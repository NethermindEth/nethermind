// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Text;
using Nethermind.EraE.Store;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Store;

public class HttpRemoteEraClientTests
{
    private const string BaseUrl = "https://example.test/erae/sepolia/";
    private const string ManifestFilename = "checksums_sha256.txt";
    private const string ValidHash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Test]
    public async Task FetchManifestAsync_WithValidEntry_ParsesEpoch()
    {
        IReadOnlyDictionary<uint, RemoteEraEntry> manifest =
            await FetchManifest($"{ValidHash}  sepolia-00000-deadbeef.erae");

        Assert.That(manifest.ContainsKey(0u), Is.True);
        Assert.That(manifest[0u].Filename, Is.EqualTo("sepolia-00000-deadbeef.erae"));
    }

    [TestCase("../sepolia-00000-deadbeef.erae", TestName = "RelativeParentTraversal")]
    [TestCase("subdir/sepolia-00000-deadbeef.erae", TestName = "NestedDirectory")]
    [TestCase("/etc/sepolia-00000-deadbeef.erae", TestName = "RootedPath")]
    public async Task FetchManifestAsync_WhenFilenameContainsPath_SkipsEntry(string hostileFilename)
    {
        IReadOnlyDictionary<uint, RemoteEraEntry> manifest =
            await FetchManifest($"{ValidHash}  {hostileFilename}");

        Assert.That(manifest, Is.Empty);
    }

    private static async Task<IReadOnlyDictionary<uint, RemoteEraEntry>> FetchManifest(string manifestBody)
    {
        StubHttpMessageHandler handler = new(manifestBody);
        using HttpClient httpClient = new(handler);
        using HttpRemoteEraClient client = new(new Uri(BaseUrl), ManifestFilename, httpClient);
        return await client.FetchManifestAsync();
    }

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain")
            });
    }
}
