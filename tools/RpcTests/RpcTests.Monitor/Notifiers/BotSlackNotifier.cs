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
        string header =
            $"""
             ❌ {_prefix} response mismatch at block `{failure.Head}`
             - Method: `{failure.Request.MethodOrUnknown}`
             - Test: `{failure.Test.Definition.FilePath}`
             """;

        try
        {
            (string name, string content)[] files =
            [
                ("request.json", failure.Request.ToPrettyString()),
                ("actual-response.json", failure.ActualResponse.ToPrettyString()),
                ("expected-response.json", failure.ExpectedResponse.ToPrettyString())
            ];

            IList<ExternalFileReference> fileRefs = await Task.WhenAll(
                files.Select(f => UploadFileAsync(f.name, f.content, ct)));

            await _slack.Files.CompleteUploadExternal(
                fileRefs, channelId: config.ChannelId, initialComment: header,
                cancellationToken: ct);
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

            if (exception is null)
            {
                await _slack.Chat.PostMessage(new Message
                {
                    Channel = config.ChannelId,
                    Text = text
                });
            }
            else
            {
                text = $"{text}\n```{exception.Message}```";

                ExternalFileReference fileRef = await UploadFileAsync(
                    "exception.txt", exception.ToString(), CancellationToken.None
                );

                await _slack.Files.CompleteUploadExternal(
                    [fileRef], channelId: config.ChannelId, initialComment: text,
                    cancellationToken: CancellationToken.None
                );
            }
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
            await _slack.Chat.PostMessage(new Message
            {
                Channel = config.ChannelId,
                Text = $"""
                        ℹ {_prefix} statistic since `{stats.Since:u}`:
                        - `{stats.HeadUpdates}` head updates
                        - `{stats.TestRuns}` tests executed
                        - `{stats.RequestRuns}` requests sent
                        - `{stats.TestFailures}` test failures
                        - `{stats.Errors}` errors
                        """
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }

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
