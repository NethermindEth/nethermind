#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Select one shard of a tests-zkevm-benchmark archive and write its blocks as ZisK guest inputs.
//
//   inputs.cs paths --index <index.json> --shard <i> --shards <n>
//   inputs.cs frame --index <index.json> --shard <i> --shards <n> --root <dir> --out <dir> --manifest <file>
//
// `paths` prints the fixture files the shard reads, so the workflow extracts only those from the
// archive. `frame` then writes each selected test's last block, the benchmarked one, as `<n>.ssz` in
// the ZisK framing (`len u64le | data | zero padding to 8`) and a manifest mapping each input to its
// test id and expected output.
//
// Tests are assigned to shards round-robin in index order, so every shard gets a similar mix of cheap
// and expensive cases and the shard count alone sets the wall-clock time.
//
// Fixture files are read as a token stream rather than loaded: at 200M gas a single file can exceed
// the size of one array.

using System.Buffers.Binary;
using System.Text.Json;

const int ZiskAlignment = 8;
const int InitialBufferSize = 1 << 20;

if (args.Length == 0 || args[0] is not ("paths" or "frame"))
    return Usage();

Dictionary<string, string> options = [];
for (int i = 1; i < args.Length; i += 2)
{
    if (!args[i].StartsWith("--") || i + 1 >= args.Length)
        return Usage();
    options[args[i][2..]] = args[i + 1];
}

string[] required = args[0] == "paths" ? ["index", "shard", "shards"] : ["index", "shard", "shards", "root", "out", "manifest"];
foreach (string name in required)
{
    if (!options.ContainsKey(name))
        return Usage();
}

if (!int.TryParse(options["shards"], out int shards) || shards <= 0 ||
    !int.TryParse(options["shard"], out int shard) || shard < 0 || shard >= shards)
{
    Console.Error.WriteLine("error: --shard must be in [0, --shards)");
    return 2;
}

List<(string Id, string JsonPath)> cases = ReadShard(options["index"], shard, shards);
if (cases.Count == 0)
{
    Console.Error.WriteLine("error: the archive index lists no blockchain_test cases for this shard");
    return 1;
}

if (args[0] == "paths")
{
    foreach (string path in cases.Select(static c => c.JsonPath).Distinct().Order(StringComparer.Ordinal))
        Console.WriteLine(path);
    return 0;
}

string outDir = options["out"];
using FileStream manifestStream = File.Create(options["manifest"]);
using Utf8JsonWriter manifest = new(manifestStream, new JsonWriterOptions { Indented = true });
manifest.WriteStartArray();

byte[] lengthPrefix = new byte[sizeof(ulong)];
byte[] padding = new byte[ZiskAlignment];
int written = 0;
foreach (IGrouping<string, (string Id, string JsonPath)> file in cases.GroupBy(static c => c.JsonPath).OrderBy(static g => g.Key, StringComparer.Ordinal))
{
    HashSet<string> wanted = [.. file.Select(static c => c.Id)];
    foreach ((string id, string input, string output) in ReadLastBlocks(Path.Combine(options["root"], file.Key), wanted))
    {
        byte[] data = Convert.FromHexString(input.AsSpan(2));
        string name = $"{written++}.ssz";
        using (FileStream stream = File.Create(Path.Combine(outDir, name)))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(lengthPrefix, (ulong)data.Length);
            stream.Write(lengthPrefix);
            stream.Write(data);
            stream.Write(padding, 0, (ZiskAlignment - data.Length % ZiskAlignment) % ZiskAlignment);
        }

        manifest.WriteStartObject();
        manifest.WriteString("input", name);
        manifest.WriteString("id", id);
        manifest.WriteString("output", output[2..]);
        manifest.WriteEndObject();
    }

    if (wanted.Count != 0)
    {
        Console.Error.WriteLine($"error: {file.Key} does not define {string.Join(", ", wanted)}");
        return 1;
    }
}

manifest.WriteEndArray();
Console.Error.WriteLine($"Wrote {written} inputs for shard {shard + 1}/{shards}");
return 0;

static int Usage()
{
    Console.Error.WriteLine("usage: inputs.cs paths --index <index.json> --shard <i> --shards <n>");
    Console.Error.WriteLine("       inputs.cs frame --index <index.json> --shard <i> --shards <n> --root <dir> --out <dir> --manifest <file>");
    return 2;
}

static List<(string Id, string JsonPath)> ReadShard(string indexPath, int shard, int shards)
{
    using FileStream stream = File.OpenRead(indexPath);
    using JsonDocument index = JsonDocument.Parse(stream);
    List<(string, string)> cases = [];
    int ordinal = 0;
    foreach (JsonElement testCase in index.RootElement.GetProperty("test_cases").EnumerateArray())
    {
        if (testCase.GetProperty("format").GetString() != "blockchain_test")
            continue;
        if (ordinal++ % shards == shard)
            cases.Add((testCase.GetProperty("id").GetString()!, testCase.GetProperty("json_path").GetString()!));
    }

    return cases;
}

// Yields each wanted test's last-block stateless input and output, removing the test from `wanted`.
// A fixture file is `{ "<test id>": { ..., "blocks": [ { ..., "statelessInputBytes": "0x..", ... } ] } }`,
// which puts the test ids at depth 1, the test's own properties at depth 2 and a block's at depth 4.
static IEnumerable<(string Id, string Input, string Output)> ReadLastBlocks(string path, HashSet<string> wanted)
{
    using FileStream stream = File.OpenRead(path);
    byte[] buffer = new byte[InitialBufferSize];
    int length = 0;
    bool final = false;
    JsonReaderState state = default;

    string? test = null, input = null, output = null;
    bool inBlocks = false;
    BlockField field = BlockField.None;
    List<(string, string, string)> found = [];

    while (!final)
    {
        int read = stream.Read(buffer, length, buffer.Length - length);
        final = read == 0;
        length += read;

        Utf8JsonReader reader = new(buffer.AsSpan(0, length), final, state);
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    string id = reader.GetString()!;
                    test = wanted.Contains(id) ? id : null;
                    input = output = null;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 2 && test is not null:
                    inBlocks = reader.ValueTextEquals("blocks"u8);
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 3 && test is not null && inBlocks:
                    input = output = null;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 4 && test is not null && inBlocks:
                    field = reader.ValueTextEquals("statelessInputBytes"u8) ? BlockField.Input
                        : reader.ValueTextEquals("statelessOutputBytes"u8) ? BlockField.Output
                        : BlockField.None;
                    break;
                case JsonTokenType.String when reader.CurrentDepth == 4 && field != BlockField.None:
                    if (field == BlockField.Input)
                        input = reader.GetString();
                    else
                        output = reader.GetString();
                    field = BlockField.None;
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 1 && test is not null:
                    if (input is null || output is null)
                        throw new InvalidDataException($"{path}: {test} has no stateless input or output in its last block");
                    found.Add((test, input, output));
                    wanted.Remove(test);
                    test = null;
                    break;
            }
        }

        int consumed = (int)reader.BytesConsumed;
        state = reader.CurrentState;
        length -= consumed;
        // A token larger than the buffer, such as a big witness hex string, leaves nothing consumable.
        if (consumed == 0 && length == buffer.Length)
            Array.Resize(ref buffer, buffer.Length * 2);
        else
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length);

        // Hand back finished tests between chunks so their inputs do not accumulate.
        foreach ((string, string, string) item in found)
            yield return item;
        found.Clear();
    }
}

enum BlockField { None, Input, Output }
