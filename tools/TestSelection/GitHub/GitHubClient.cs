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
/// The GitHub REST API over HTTP. Rate limits and transient failures are retried, never turned into empty results: a
/// lost log would record real failures as infra. A write is retried only when GitHub did not process it (a rate limit),
/// so a retry never opens an issue twice.
/// </summary>
/// <param name="reserveShare">
/// The share of the token's hourly budget left to the repository's other jobs: the bulk commands pause below it until
/// the reset. Every response reports the budget, so this costs no extra call. Commands on the critical path of a pull
/// request pass zero and never wait for it.
/// </param>
/// <param name="delay">Waits between attempts and for the budget; tests pass one that only records.</param>
public sealed partial class GitHubClient(HttpClient http, string repository, double reserveShare = 0, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : IGitHubApi, IDisposable
{
    public const double BulkReserveShare = 0.3;
    private const int Attempts = 4;

    private readonly SemaphoreSlim _budgetGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private long _budgetResetsAt;

    public string Repository => repository;

    public static GitHubClient FromEnvironment(string repository, double reserveShare = 0)
    {
        string token = Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? throw new InvalidOperationException("GH_TOKEN or GITHUB_TOKEN must hold a GitHub token");
        HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("nethermind-test-selection", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubClient(http, repository, reserveShare);
    }

    public async Task<JsonNode> GetAsync(string path, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        return await ReadJsonAsync(response, cancellation) ?? throw new GitHubException($"GET {path}: empty response");
    }

    public async IAsyncEnumerable<JsonNode> GetPagesAsync(string path, [EnumeratorCancellation] CancellationToken cancellation)
    {
        for (string? next = path; next is not null;)
        {
            string current = next;
            using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, current), cancellation);
            next = NextPage(response);
            yield return await ReadJsonAsync(response, cancellation) ?? throw new GitHubException($"GET {current}: empty page");
        }
    }

    public async Task<string> GetTextAsync(string path, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        return await response.Content.ReadAsStringAsync(cancellation);
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellation);
        await response.Content.CopyToAsync(destination, cancellation);
    }

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await SendAsync(
            () => new HttpRequestMessage(method, path) { Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") },
            cancellation);
        return await ReadJsonAsync(response, cancellation);
    }

    public void Dispose()
    {
        http.Dispose();
        _budgetGate.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken cancellation)
    {
        for (int attempt = 1; ; attempt++)
        {
            await WaitForBudgetAsync(cancellation);
            using HttpRequestMessage message = request();
            Attempt result = await TrySendAsync(message, attempt, cancellation);
            if (result.Response is { } response)
            {
                return response;
            }

            if (result.RetryAfter is not { } wait || attempt == Attempts)
            {
                throw new GitHubException(result.Failure, result.Status);
            }

            await _delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, cancellation);
        }
    }

    /// <summary>One attempt. The body is read here, so a connection that breaks mid-body is retried like any other.</summary>
    private async Task<Attempt> TrySendAsync(HttpRequestMessage message, int attempt, CancellationToken cancellation)
    {
        bool idempotent = message.Method == HttpMethod.Get;
        try
        {
            HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellation);
            RecordBudget(response);
            if (response.IsSuccessStatusCode)
            {
                return new Attempt(response, string.Empty, response.StatusCode, null);
            }

            using (response)
            {
                string body = await response.Content.ReadAsStringAsync(cancellation);
                return new Attempt(null, $"{message.Method} {message.RequestUri}: {(int)response.StatusCode} {body}", response.StatusCode,
                    RetryDelay(response, body, attempt, idempotent));
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            // A write that failed in transit may have happened; only a read is safe to send again.
            return new Attempt(null, $"{message.Method} {message.RequestUri}: {error.Message}", null, idempotent ? TimeSpan.FromSeconds(5 * attempt) : null);
        }
    }

    /// <summary>How long to wait before the next attempt, or <c>null</c> when retrying cannot help or is not safe.</summary>
    private static TimeSpan? RetryDelay(HttpResponseMessage response, string body, int attempt, bool idempotent)
    {
        // A rate limit means GitHub did not process the request, so even a write may be sent again.
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        bool limited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
        if (limited && Remaining(response) == 0)
        {
            return UntilReset(response);
        }

        if (limited && (response.StatusCode == HttpStatusCode.TooManyRequests || body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)))
        {
            return TimeSpan.FromSeconds(60 * attempt);
        }

        return response.StatusCode >= HttpStatusCode.InternalServerError && idempotent ? TimeSpan.FromSeconds(5 * attempt) : null;
    }

    private void RecordBudget(HttpResponseMessage response)
    {
        if (Remaining(response) is { } remaining && Header(response, "x-ratelimit-limit") is { } limit
            && remaining < limit * reserveShare && Header(response, "x-ratelimit-reset") is { } reset)
        {
            Interlocked.Exchange(ref _budgetResetsAt, reset);
        }
    }

    private async Task WaitForBudgetAsync(CancellationToken cancellation)
    {
        if (Interlocked.Read(ref _budgetResetsAt) == 0)
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
                await Console.Error.WriteLineAsync($"GitHub budget below {reserveShare:P0}; waiting {wait:hh\\:mm\\:ss} for the reset");
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

    /// <summary>The outcome of one attempt: a successful response, or why it failed and when to try again.</summary>
    private readonly record struct Attempt(HttpResponseMessage? Response, string Failure, HttpStatusCode? Status, TimeSpan? RetryAfter);
}
