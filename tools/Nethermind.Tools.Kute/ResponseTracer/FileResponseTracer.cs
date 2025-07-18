// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.ResponseTracer;

public sealed class FileResponseTracer : IResponseTracer
{
    private readonly string _tracesFilePath;

    public FileResponseTracer(string tracesFilePath)
    {
        _tracesFilePath = tracesFilePath;
    }

    public async Task TraceResponse(JsonRpc.Response response)
    {
        await using StreamWriter sw = File.Exists(_tracesFilePath)
            ? File.AppendText(_tracesFilePath)
            : File.CreateText(_tracesFilePath);

        await sw.WriteLineAsync(response.Json.ToString() ?? "null");
    }
}
