// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.ResponseTracer;

public class FileResponseTracer : IResponseTracer
{
    private readonly string _tracesFilePath;

    public FileResponseTracer(string tracesFilePath)
    {
        _tracesFilePath = tracesFilePath;
    }

    public async Task TraceResponse(JsonDocument? response)
    {
        await using StreamWriter sw = File.Exists(_tracesFilePath)
            ? File.AppendText(_tracesFilePath)
            : File.CreateText(_tracesFilePath);

        await sw.WriteLineAsync(response?.RootElement.ToString() ?? "null");
    }
}
