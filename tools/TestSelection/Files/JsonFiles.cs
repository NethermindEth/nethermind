// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.TestSelection.Files;

/// <summary>
/// JSON and JSON-lines files, gzipped when the name ends in <c>.gz</c>. A write goes to a temporary file first, so a
/// run stopped mid-write (timeout, cancellation) leaves the previous file instead of a truncated one.
/// </summary>
public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static T Read<T>(string path)
    {
        using Stream stream = OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"{path} holds no {typeof(T).Name}");
    }

    public static JsonNode ReadNode(string path)
    {
        using Stream stream = OpenRead(path);
        return JsonNode.Parse(stream) ?? throw new InvalidDataException($"{path} holds no JSON");
    }

    public static void Write<T>(string path, T value) =>
        WriteAtomically(path, stream => JsonSerializer.Serialize(stream, value, Options));

    public static IEnumerable<string> ReadLines(string path)
    {
        using StreamReader reader = new(OpenRead(path));
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    public static void WriteLines(string path, IEnumerable<string> lines) =>
        WriteAtomically(path, stream =>
        {
            using StreamWriter writer = new(stream);
            foreach (string line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        });

    private static Stream OpenRead(string path)
    {
        FileStream file = File.OpenRead(path);
        return IsGzip(path) ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    private static void WriteAtomically(string path, Action<Stream> write)
    {
        string temporary = path + ".tmp";
        using (FileStream file = File.Create(temporary))
        using (Stream stream = IsGzip(path) ? new GZipStream(file, CompressionLevel.Optimal) : file)
        {
            write(stream);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsGzip(string path) => path.EndsWith(".gz", StringComparison.Ordinal);
}
