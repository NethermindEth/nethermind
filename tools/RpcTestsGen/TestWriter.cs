// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpcTestsGen;

// not thread-safe
public sealed class TestWriter : IAsyncDisposable
{
    private string? _currentFile;
    private FileStream? _fileStream;
    private Utf8JsonWriter? _jsonWriter;

    private readonly List<string> _outputFiles = [];
    public IReadOnlyCollection<string> OutputFiles => _outputFiles;

    public async Task WriteAsync(TestCase testCase)
    {
        string testFile = testCase.Location.FilePath;
        if (_currentFile != testFile)
        {
            await CloseCurrentFileAsync();
            OpenNewFile(testFile);
        }

        WriteTest(testCase.Request, testCase.Response);
    }

    public ValueTask DisposeAsync() => CloseCurrentFileAsync();

    private void OpenNewFile(string inputFilePath)
    {
        _currentFile = inputFilePath;

        string outputPath = BuildOutputPath(inputFilePath);
        _outputFiles.Add(outputPath);

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
        _currentFile = null;
    }

    private static string BuildOutputPath(string inputPath)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        string name = Path.ChangeExtension(Path.GetFileName(inputPath), ".test.json");
        return Path.Combine(dir, name);
    }
}
