// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nethermind.TestSelection.GitHub;

/// <summary>
/// The GitHub REST API over HTTP. Rate limits are waited out, never turned into empty results: a lost log would
/// record real failures as infra. The token's hourly budget is shared with the other jobs of the repository, so calls
/// pause while less than <see cref="ReserveShare"/> of it remains; every response reports the budget, so this costs no
/// extra call.
/// </summary>
/// <param name="delay">Waits between attempts and for the budget; tests pass one that only records.</param>
public sealed partial class GitHubClient(HttpClient http, string repository, Func<TimeSpan, CancellationToken, Task>? delay = null) : IGitHubApi, IDisposable
{
    public const double ReserveShare = 0.3;
    private const int Attempts = 4;

    private readonly SemaphoreSlim _budgetGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private long _budgetResetsAt;

    public string Repository => repository;

    public static GitHubClient FromEnvironment(string repository)
    {
        string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN must hold a GitHub token");
        HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("nethermind-test-selection", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubClient(http, repository);
    }

    public async Task<JsonNode> GetAsync(string path, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        return await ReadJsonAsync(response, cancellation) ?? throw new GitHubException($"GET {path}: empty response");
    }

    public async IAsyncEnumerable<JsonNode> GetPagesAsync(string path, [EnumeratorCancellation] CancellationToken cancellation)
    {
        for (string? next = path; next is not null;)
        {
            string current = next;
            using HttpResponseMessage response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, current), cancellation);
            next = NextPage(response);
            yield return await ReadJsonAsync(response, cancellation) ?? throw new GitHubException($"GET {current}: empty page");
        }
    }

    public async Task<string> GetTextAsync(string path, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        await response.Content.CopyToAsync(destination, cancellation);
    }

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode body, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendWithRetriesAsync(
            () => new HttpRequestMessage(method, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") },
            cancellation);
        return await ReadJsonAsync(response, cancellation);
    }

    public void Dispose()
    {
        http.Dispose();
        _budgetGate.Dispose();
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(Func<HttpRequestMessage> request, CancellationToken cancellation)
    {
        string failure = string.Empty;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            await WaitForBudgetAsync(cancellation);
            using HttpRequestMessage message = request();
            HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
            RecordBudget(response);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            failure = $"{message.Method} {message.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellation)}";
            TimeSpan? retryAfter = RetryDelay(response, attempt);
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            if (retryAfter is null)
            {
                throw new GitHubException(failure, status);
            }

            await _delay(retryAfter.Value, cancellation);
        }

        throw new GitHubException(failure);
    }

    /// <summary>How long to wait before the next attempt, or <c>null</c> when retrying cannot help.</summary>
    private static TimeSpan? RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests when Remaining(response) == 0 => UntilReset(response),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => TimeSpan.FromSeconds(60 * attempt),
            >= HttpStatusCode.InternalServerError => TimeSpan.FromSeconds(5 * attempt),
            _ => null,
        };
    }

    private void RecordBudget(HttpResponseMessage response)
    {
        if (Remaining(response) is { } remaining && Header(response, "x-ratelimit-limit") is { } limit && remaining < limit * ReserveShare
            && Header(response, "x-ratelimit-reset") is { } reset)
        {
            Interlocked.Exchange(ref _budgetResetsAt, reset);
        }
    }

    private async Task WaitForBudgetAsync(CancellationToken cancellation)
    {
        long resetsAt = Interlocked.Read(ref _budgetResetsAt);
        if (resetsAt == 0)
        {
            return;
        }

        await _budgetGate.WaitAsync(cancellation);
        try
        {
            // Another caller, or a rate-limited attempt, may have waited for this reset already.
            DateTimeOffset resetAt = DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _budgetResetsAt));
            if (resetAt > DateTimeOffset.UtcNow)
            {
                TimeSpan wait = resetAt - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                await Console.Error.WriteLineAsync($"GitHub budget below {ReserveShare:P0}; waiting {wait:hh\\:mm\\:ss} for the reset");
                await _delay(wait, cancellation);
            }

            Interlocked.Exchange(ref _budgetResetsAt, 0);
        }
        finally
        {
            _budgetGate.Release();
        }
    }

    private static TimeSpan UntilReset(HttpResponseMessage response) =>
        Header(response, "x-ratelimit-reset") is { } reset
            ? DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)
            : TimeSpan.FromMinutes(1);

    private static long? Remaining(HttpResponseMessage response) => Header(response, "x-ratelimit-remaining");

    private static long? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) && long.TryParse(values.FirstOrDefault(), out long value) ? value : null;

    private static string? NextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out IEnumerable<string>? links) && NextLink().Match(string.Join(',', links)) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        string body = await response.Content.ReadAsStringAsync(cancellation);
        return body.Length == 0 ? null : JsonNode.Parse(body);
    }

    [GeneratedRegex(@"<([^>]+)>;\s*rel=""next""")]
    private static partial Regex NextLink();
}
