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

// TODO: rate limit a bit
internal class BotSlackNotifier(BotSlackConfig config) : INotifier
{
    private readonly HttpClient _httpClient = new();
    private readonly ISlackApiClient _slack = new SlackServiceBuilder()
        .UseApiToken(config.BotToken)
        .GetApiClient();

    public async Task NotifyFailureAsync(TestFailure failure, CancellationToken ct)
    {
        string header =
            $"""
             *RPC response mismatch* at block #{failure.Head:#}
             Test: `{failure.Test.Definition.FilePath}`
             Method: `{failure.Request.MethodOrUnknown}`
             """;

        try
        {
            (string name, string content)[] files =
            [
                ("request.json", failure.Request.ToPrettyString()),
                ("target-response.json", failure.TargetResponse.ToPrettyString()),
                ("reference-response.json", failure.ReferenceResponse.ToPrettyString())
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

    public async Task NotifyErrorAsync(string message)
    {
        try
        {
            await _slack.Chat.PostMessage(new Message
            {
                Channel = config.ChannelId,
                Text = $"*RPC monitoring error*:\n```{message}```"
            });
        }
        catch (Exception ex)
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
}
