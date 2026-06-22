// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using SlackNet;
using SlackNet.WebApi;

namespace Nethermind.RpcTests.Monitor.Notifiers;

internal class BotSlackConfig
{
    public required string BotToken { get; init; }
    public required string ChannelId { get; init; }
}

internal sealed class BotSlackNotifier(string name, BotSlackConfig config) : INotifier
{
    private readonly HttpClient _httpClient = new();
    private readonly ISlackApiClient _slack = new SlackServiceBuilder()
        .UseApiToken(config.BotToken)
        .GetApiClient();

    private readonly string _prefix = $"*RPC monitor `{name}`*";

    public async Task NotifyFailureAsync(TestFailure failure, CancellationToken ct)
    {
        try
        {
            string message =
                $"""
                 ❌ {_prefix} response mismatch
                 - Method: `{failure.Request.MethodOrUnknown}`
                 - Test: `{failure.Test.Definition.FilePath}`
                 - Head: `{failure.Head}`
                 """;

            List<(string name, string content)> files =
            [
                ("request.json", failure.Request.ToPrettyString()),
                ("actual-response.json", failure.ActualResponse.ToPrettyString()),
                ("expected-response.json", failure.ExpectedResponse.ToPrettyString())
            ];

            if (BuildReorgsFile(failure.RecentReorgs) is { } reorgsFile)
                files.Add(reorgsFile);

            await PostAsync(message, files, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }

    public async Task NotifyErrorAsync(string error, Exception? exception = null)
    {
        try
        {
            string text = $"⚠ {_prefix}\n{error}";
            List<(string name, string content)> files = [];

            if (exception is not null)
            {
                text = $"{text}\n```{exception.Message}```";

                files.Add(("exception.txt", exception.ToString()));

                foreach (object key in exception.Data.Keys)
                    files.Add((key.ToString()!, exception.Data[key]?.ToString() ?? ""));
            }

            await PostAsync(text, files, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }

    public async Task NotifyStatsAsync(MonitorStats stats, CancellationToken ct)
    {
        try
        {
            string text =
                $"""
                 ℹ {_prefix} statistic since `{stats.Since:u}`:
                 - `{stats.HeadUpdates}` head updates, `{stats.Reorgs}` reorgs
                 - `{stats.TestRuns}` tests executed
                 - `{stats.RequestRuns}` requests sent
                 - `{stats.TestFailures}` test failures
                 - `{stats.Errors}` errors
                 """;

            List<(string name, string content)> files = [];
            if (BuildReorgsFile(stats.RecentReorgs) is { } reorgsFile)
                files.Add(reorgsFile);

            await PostAsync(text, files, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }

    private async Task PostAsync(string comment, IReadOnlyList<(string name, string content)> files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            await _slack.Chat.PostMessage(new Message { Channel = config.ChannelId, Text = comment }, ct);
            return;
        }

        IList<ExternalFileReference> fileRefs = await Task.WhenAll(
            files.Select(f => UploadFileAsync(f.name, f.content, ct)));

        await _slack.Files.CompleteUploadExternal(
            fileRefs, channelId: config.ChannelId, initialComment: comment,
            cancellationToken: ct);
    }

    private static (string name, string content)? BuildReorgsFile(IReadOnlyList<ReorgEntry> reorgs) =>
        reorgs.Count == 0
            ? null
            : ("recent-reorgs.txt", string.Join('\n', reorgs.Select(static r => r.ToString())));

    private async Task<ExternalFileReference> UploadFileAsync(string filename, string content, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        UploadUrlExternalResponse urlResponse = await _slack.Files.GetUploadUrlExternal(
            filename, bytes.Length, cancellationToken: ct);

        using ByteArrayContent uploadContent = new(bytes);
        HttpResponseMessage response = await _httpClient.PostAsync(urlResponse.UploadUrl, uploadContent, ct);
        response.EnsureSuccessStatusCode();

        return new ExternalFileReference { Id = urlResponse.FileId };
    }

    public void Dispose() => _httpClient.Dispose();
}
