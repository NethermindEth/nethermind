// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFormat;
using SmartFormat.Core.Parsing;

namespace Nethermind.RpcTests.Generator;

// not thread-safe
public sealed class TestWriter(Filter filter, Format outputFormat) : IAsyncDisposable
{
    private int _testN;
    private string? _currentOutFile;
    private FileStream? _fileStream;
    private Utf8JsonWriter? _jsonWriter;

    public int OutputCount { get; private set; }

    public async Task WriteAsync(TestCase testCase)
    {
        if (!filter.IncludeResponse(testCase.Response)) return;

        string outFile = GetOutputPath(testCase);
        if (outFile != _currentOutFile)
        {
            await CloseCurrentFileAsync();
            OpenNewFile(outFile);
        }

        WriteTest(testCase.Request, testCase.Response);
    }

    public ValueTask DisposeAsync() => CloseCurrentFileAsync();

    private void OpenNewFile(string outputPath)
    {
        _currentOutFile = outputPath;
        OutputCount++;

        _fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        _jsonWriter = new Utf8JsonWriter(_fileStream, new JsonWriterOptions { Indented = true });

        _jsonWriter.WriteStartArray();
    }

    private void WriteTest(JsonNode request, JsonNode response)
    {
        _jsonWriter!.WriteStartObject();
        _jsonWriter.WritePropertyName("request");
        request.WriteTo(_jsonWriter);
        _jsonWriter.WritePropertyName("response");
        response.WriteTo(_jsonWriter);
        _jsonWriter.WriteEndObject();
    }

    private async ValueTask CloseCurrentFileAsync()
    {
        if (_jsonWriter is null) return;

        _jsonWriter.WriteEndArray();

        await _jsonWriter.FlushAsync();
        await _jsonWriter.DisposeAsync();
        await _fileStream!.DisposeAsync();

        _jsonWriter = null;
        _fileStream = null;
        _currentOutFile = null;
    }

    private string GetOutputPath(TestCase testCase)
    {
        testCase.TestN = ++_testN; // TODO: avoid mutating test case
        return Smart.Default.Format(outputFormat, testCase);
    }
}
