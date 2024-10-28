// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.ResponseTracer;

public class FileResponseTracer(string tracesFilePath) : IResponseTracer
{
    private readonly string _tracesFilePath = tracesFilePath ?? throw new ArgumentNullException(nameof(tracesFilePath));

    public async Task TraceResponse(JsonDocument? response)
    {
        await using StreamWriter sw = File.Exists(_tracesFilePath)
            ? File.AppendText(_tracesFilePath)
            : File.CreateText(_tracesFilePath!);

        await sw.WriteLineAsync(response?.RootElement.ToString() ?? "null");
    }
}
