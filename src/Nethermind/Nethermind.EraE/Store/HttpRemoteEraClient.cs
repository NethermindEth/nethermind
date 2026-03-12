// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Exceptions;
using Nethermind.Logging;

namespace Nethermind.EraE.Store;

/// <summary>
/// Downloads eraE archive files from a remote HTTP server.
/// The server must expose a checksum manifest in standard sha256sum format:
///   {hex-sha256}  {filename}
/// one entry per line, e.g.:
///   50e29a878a489ef195d4df52072a4524b7bba8b74d65c1e5f5401f914c96366a  sepolia-00000-8e3e7dc9.erae
/// </summary>
public sealed class HttpRemoteEraClient : IRemoteEraClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly string _manifestFilename;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger;

    public HttpRemoteEraClient(Uri baseUrl, string manifestFilename, HttpClient? httpClient = null, ILogManager? logManager = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFilename);

        _baseUrl = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _manifestFilename = manifestFilename;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<HttpRemoteEraClient>();
    }

    public async Task<IReadOnlyDictionary<int, RemoteEraEntry>> FetchManifestAsync(CancellationToken cancellation = default)
    {
        Uri manifestUri = new(_baseUrl, _manifestFilename);

        if (_logger.IsInfo) _logger.Info($"Fetching eraE manifest from {manifestUri}");

        using HttpResponseMessage response = await _httpClient.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellation);

        if (!response.IsSuccessStatusCode)
            throw new EraException($"Failed to fetch eraE manifest from {manifestUri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        using StreamReader reader = new(stream);

        Dictionary<int, RemoteEraEntry> manifest = new();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Standard sha256sum format: "{hash}  {filename}" (two spaces)
            int separatorIdx = line.IndexOf("  ", StringComparison.Ordinal);
            if (separatorIdx < 0) continue;

            string hashHex = line[..separatorIdx].Trim();
            string filename = line[(separatorIdx + 2)..].Trim();

            if (!TryParseEpoch(filename, out int epoch)) continue;
            if (!TryParseHex(hashHex, out byte[] sha256)) continue;

            manifest[epoch] = new RemoteEraEntry(filename, sha256);
        }

        return manifest;
    }

    public async Task DownloadFileAsync(string filename, string destinationPath, CancellationToken cancellation = default)
    {
        Uri fileUri = new(_baseUrl, filename);
        string tmpPath = destinationPath + ".tmp";

        string? destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        if (_logger.IsInfo) _logger.Info($"Downloading eraE file {filename} from {fileUri}");

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, cancellation);

            if (!response.IsSuccessStatusCode)
                throw new EraException($"Failed to download eraE file '{filename}': HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

            long? contentLength = response.Content.Headers.ContentLength;

            await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellation);
            await using FileStream fileStream = new(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            await httpStream.CopyToAsync(fileStream, cancellation);

            double mb = (contentLength ?? fileStream.Length) / 1_048_576.0;
            if (_logger.IsInfo) _logger.Info($"Downloaded eraE file {filename} ({mb:F1} MB)");
        }
        catch
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
            throw;
        }

        File.Move(tmpPath, destinationPath, overwrite: true);
    }

    private static bool TryParseEpoch(string filename, out int epoch)
    {
        epoch = 0;
        // Expected: {network}-{epoch:05d}-{hash}.erae
        ReadOnlySpan<char> name = Path.GetFileNameWithoutExtension(filename.AsSpan());
        int first = name.IndexOf('-');
        if (first < 0) return false;
        int second = name[(first + 1)..].IndexOf('-');
        if (second < 0) return false;
        ReadOnlySpan<char> epochPart = name[(first + 1)..(first + 1 + second)];
        return int.TryParse(epochPart, out epoch) && epoch >= 0;
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length % 2 != 0) return false;
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
