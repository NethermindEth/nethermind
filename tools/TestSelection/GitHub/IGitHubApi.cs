// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.TestSelection.GitHub;

/// <summary>The GitHub REST calls this tool makes, relative to <c>https://api.github.com/</c>.</summary>
public interface IGitHubApi
{
    string Repository { get; }

    Task<JsonNode> GetAsync(string path, CancellationToken cancellation);

    /// <summary>Every page of a paginated list, following the <c>Link</c> header.</summary>
    IAsyncEnumerable<JsonNode> GetPagesAsync(string path, CancellationToken cancellation);

    Task<string> GetTextAsync(string path, CancellationToken cancellation);

    Task DownloadAsync(string path, Stream destination, CancellationToken cancellation);

    Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode body, CancellationToken cancellation);
}
