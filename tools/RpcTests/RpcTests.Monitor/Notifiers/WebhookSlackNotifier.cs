// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;

namespace Nethermind.RpcTests.Monitor.Notifiers;

internal sealed class WebhookSlackNotifier(string webhookUrl) : INotifier
{
    private readonly HttpClient _client = new();

    public Task NotifyFailureAsync(TestFailure failure, CancellationToken ct)
    {
        string text =
            $"""
             *RPC response mismatch* at block #{failure.Head:#}
             Test: `{failure.Test.Definition.FilePath}`
             Method: `{failure.Request.MethodOrUnknown}`
             """;

        object[] attachments =
        [
            MakeAttachment("request.json", "#d3d3d3", failure.Request.ToPrettyString()),
            MakeAttachment("actual-response.json", "#ff6b35", failure.ActualResponse.ToPrettyString()),
            MakeAttachment("expected-response.json", "#36a64f", failure.ExpectedResponse.ToPrettyString())
        ];

        return PostAsync(new { text, attachments }, ct);
    }

    public Task NotifyErrorAsync(string message) =>
        PostAsync(new { text = $"*RPC monitoring error*:\n```{message}```" }, CancellationToken.None);

    private static object MakeAttachment(string title, string color, string json) => new
    {
        color,
        title,
        text = $"```\n{json}\n```",
        mrkdwn_in = new[] { "text" }
    };

    private async Task PostAsync(object payload, CancellationToken ct)
    {
        using JsonContent content = JsonContent.Create(payload);
        try
        {
            using HttpResponseMessage response = await _client.PostAsync(webhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"Slack notification failed: {response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }

    public void Dispose() => _client.Dispose();
}
