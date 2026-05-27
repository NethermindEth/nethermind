// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;

namespace RpcTestsMon;

internal interface INotifier
{
    Task NotifyMismatchAsync(MismatchInfo info, CancellationToken ct);
    Task NotifyErrorAsync(string message);
}

internal class SlackNotifier(string webhookUrl) : INotifier
{
    private readonly HttpClient _client = new();

    public Task NotifyMismatchAsync(MismatchInfo info, CancellationToken ct) => NotifyAsync(
        $"""
         *RPC mismatch* at block #{info.Head}`)
         Test: `{info.Test.Definition.FilePath}`

         Request:
         ```
         {info.Request.ToCompactString()}
         ```

         Target:
         ```
         {info.TargetResponse.ToCompactString()}
         ```

         Reference:
         ```
         {info.ReferenceResponse.ToCompactString()}
         ```
         """, ct);

    public Task NotifyErrorAsync(string message) => NotifyAsync(
        $"""
         *RPC monitoring error*:
         ```
         {message}
         ```
         """, CancellationToken.None);

    private async Task NotifyAsync(string text, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { text });
        using StringContent content = new(payload, Encoding.UTF8, "application/json");
        try
        {
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
