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

    public async Task TraceResponse(JsonRpc.Response response, CancellationToken token = default)
    {
        await using StreamWriter sw = File.Exists(_tracesFilePath)
            ? File.AppendText(_tracesFilePath)
            : File.CreateText(_tracesFilePath);

        var content = response.Json.ToString() ?? "null";
        await sw.WriteLineAsync(MemoryExtensions.AsMemory(content), token);
    }
}
