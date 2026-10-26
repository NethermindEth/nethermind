#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include CorpusParity.Helpers.cs
#:include RpcJson.Helpers.cs

using System.Text.Json;
using System.Text.Json.Nodes;

#if !SCRIPT_TEST
if (args.Length != 2) { Console.Error.WriteLine("Usage: PrepareEthCallCorpus.cs <source.jsonl[.gz]> <destination>"); return 2; }
try
{
    var classes = PrepareEthCallCorpus.Convert(args[0], args[1], Environment.GetEnvironmentVariable);
    Console.WriteLine($"corpus classes: {classes.Count} ({string.Join(", ", classes.Select(pair => $"{pair.Key}={pair.Value}"))})");
    return 0;
}
catch (CorpusParityError error) { Console.Error.WriteLine($"error: {error.Message}"); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"error: conversion failed ({exception.GetType().Name})"); return 1; }
#endif

internal static class PrepareEthCallCorpus
{
    internal static string Selector(JsonArray parameters)
    {
        var call = parameters.Count > 0 ? parameters[0] as JsonObject : null;
        var value = call?["data"];
        if (value is null || value.ToString().Length == 0) value = call?["input"];
        return value is JsonValue literal && literal.TryGetValue<string>(out var data) && data.StartsWith("0x", StringComparison.Ordinal) && data.Length >= 10 ? data[..10].ToLowerInvariant() : "none";
    }

    internal static Dictionary<string, int> Convert(string source, string destination, Func<string, string?> environment)
    {
        Directory.CreateDirectory(destination);
        using var parity = new CorpusParity(environment);
        var (method, options) = parity.Rewrite();
        var writers = new Dictionary<string, (string Path, int Count)>();
        var number = 0;
        try
        {
            using var reader = CorpusParity.OpenCorpus(source);
            while (reader.ReadLine() is { } line)
            {
                number++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(line); }
                catch (JsonException) { throw new CorpusParityError($"{source}: line {number}: invalid JSON"); }
                if (parsed is not JsonObject record) throw new CorpusParityError($"{source}: line {number}: record must be a JSON object");
                if (CorpusParity.Text(record["method"]) != "eth_call") throw new CorpusParityError($"{source}: line {number}: method must be exactly 'eth_call'");
                if (record["params"] is not JsonArray parameters) throw new CorpusParityError($"{source}: line {number}: params must be a JSON array");
                JsonArray rewritten;
                try { rewritten = CorpusParity.RewriteRecord(parameters, method, options); }
                catch (CorpusParityError error) { throw new CorpusParityError($"{source}: line {number}: {error.Message}"); }
                var output = new JsonObject { ["method"] = method, ["params"] = rewritten };
                var selector = Selector(rewritten);
                if (!writers.TryGetValue(selector, out var writer))
                {
                    var path = Path.Combine(destination, $".class.{Guid.NewGuid():N}.tmp");
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.WriteByte((byte)'[');
                    writer = (path, 0);
                    writers[selector] = writer;
                }
                File.AppendAllText(writer.Path, (writer.Count > 0 ? "," : "") + output.ToJsonString());
                writers[selector] = (writer.Path, writer.Count + 1);
            }
            if (writers.Count == 0) throw new CorpusParityError($"{source}: line 0: input contains no nonblank JSON records");
            foreach (var writer in writers.Values)
            {
                using var file = new FileStream(writer.Path, FileMode.Append, FileAccess.Write);
                file.Write("]\n"u8);
                file.Flush(flushToDisk: true);
            }
            var classes = new Dictionary<string, int>();
            var rank = 0;
            foreach (var writer in writers.Values.OrderByDescending(writer => writer.Count))
            {
                var name = $"class_{++rank}";
                SetMode(writer.Path);
                File.Move(writer.Path, Path.Combine(destination, name + ".json"), overwrite: true);
                classes[name] = writer.Count;
            }
            var manifest = Path.Combine(destination, "classes.json");
            var document = new JsonObject();
            foreach (var (key, count) in classes) document[key] = count;
            CorpusParity.Save(manifest, document);
            SetMode(manifest);
            return classes;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.DecoderFallbackException or UnauthorizedAccessException)
        { throw new CorpusParityError($"{source}: line {number}: unable to read source ({exception.GetType().Name})"); }
        finally
        {
            foreach (var writer in writers.Values) File.Delete(writer.Path);
        }
    }

    private static void SetMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

}
