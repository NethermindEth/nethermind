// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Json;

namespace RpcTestsMon;

internal interface INotifier
{
    Task NotifyMismatchAsync(MismatchInfo info, CancellationToken ct);
    Task NotifyErrorAsync(string message);
}

internal class SlackNotifier(string webhookUrl) : INotifier
{
    private readonly HttpClient _client = new();

    public Task NotifyMismatchAsync(MismatchInfo info, CancellationToken ct)
    {
        string text =
            $"""
             *RPC mismatch* at block #{info.Head:#}
             Test: `{info.Test.Definition.FilePath}`
             Method: `{info.Request.MethodOrUnknown}`
             """;

        object[] attachments =
        [
            MakeAttachment("request.json", "#d3d3d3", info.Request.ToPrettyString()),
            MakeAttachment("target-response.json", "#ff6b35", info.TargetResponse.ToPrettyString()),
            MakeAttachment("reference-response.json", "#36a64f", info.ReferenceResponse.ToPrettyString())
        ];

        return NotifyAsync(new { text, attachments }, ct);
    }

    public Task NotifyErrorAsync(string message) => NotifyAsync(
        new {text = $"*RPC monitoring error*:\n```{message}```"},
        CancellationToken.None
    );

    private static object MakeAttachment(string title, string color, string json) => new
    {
        color,
        title,
        text = $"```\n{json}\n```",
        mrkdwn_in = new[] { "text" }
    };

    private async Task NotifyAsync(object payload, CancellationToken ct)
    {
        try
        {
            using JsonContent content = JsonContent.Create(payload);
            HttpResponseMessage response = await _client.PostAsync(webhookUrl, content, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"Slack notification failed: {response.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Slack notification error: {ex.Message}");
        }
    }
}
