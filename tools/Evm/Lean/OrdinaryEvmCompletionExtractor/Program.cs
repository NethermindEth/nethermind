// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class Program
{
    private const string Package = "Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor";

    private static int Main(string[] args)
    {
        bool readOnly = args.Length == 2 && args[0] is "--audit" or "--source-audit" or "--inspect-bindings";
        bool artifactMode = args.Length == 3 && args[0] is "--extract" or "--check";
        if (!readOnly && !artifactMode)
        {
            Console.Error.WriteLine("Usage: OrdinaryEvmCompletionExtractor --audit|--source-audit <repository-root>");
            Console.Error.WriteLine("       OrdinaryEvmCompletionExtractor --extract|--check <repository-root> <artifact-directory>");
            Console.Error.WriteLine("Only the conditional ordinary continuation is covered; complete production refinement remains open.");
            return 2;
        }

        try
        {
            string root = Path.GetFullPath(args[1]);
            int sources = Audit(root, "source-map.json");
            int upstream = Audit(root, "upstream-artifacts.json");
            int dependencies = Audit(root, "stage-dependencies.json");
            Console.WriteLine($"Pinned identity audit passed: {sources} behavior-map files, {upstream} upstream artifacts and {dependencies} stage dependencies.");
            if (artifactMode)
            {
                string output = Path.GetFullPath(args[2]);
                if (args[0] == "--extract") Artifacts.Extract(root, output);
                else Artifacts.Check(root, output);
                Console.WriteLine("Conditional completion artifacts " + (args[0] == "--extract" ? "emitted." : "match the current source and dependencies."));
                Console.WriteLine("No end-to-end production preparation or VM execution claim is made.");
                return 0;
            }
            if (args[0] is "--source-audit" or "--inspect-bindings")
            {
                SourceModel source = args[0] == "--inspect-bindings" ? SourceAdmission.InspectPinnedBaseline(root) : SourceAdmission.Read(root);
                Console.WriteLine($"Conditional source audit: {source.Compiler.Sources.Length} sources, {source.Compiler.References.Length} references, {source.Compiler.Members.Length} roots, {source.Plan.Expressions.Length} typed expression sites.");
                if (args[0] == "--inspect-bindings")
                    Console.WriteLine(JsonSerializer.Serialize(SemanticBindings.Describe(source), CompilerReferences.JsonOptions));
            }
            Console.WriteLine("Identity checks alone do not establish the conditional refinement; run the complete package gate.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or AdmissionException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Audit(string root, string resource)
    {
        Assembly assembly = typeof(Program).Assembly;
        using Stream stream = assembly.GetManifestResourceStream($"{Package}.{resource}")
            ?? throw new InvalidDataException($"Missing embedded identity map: {resource}");
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement map = document.RootElement;
        if (map.GetProperty("schemaVersion").GetInt32() != 1
            || map.GetProperty("package").GetString() != "OrdinaryEvmCompletionExtractor"
            || map.GetProperty("acceptanceState").GetString() != "static-draft-unreviewed")
        {
            throw new InvalidDataException($"Invalid static identity map: {resource}");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (JsonElement identity in map.GetProperty("files").EnumerateArray())
        {
            string relative = identity.GetProperty("path").GetString()
                ?? throw new InvalidDataException("Missing source path.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !paths.Add(path))
            {
                throw new InvalidDataException($"Invalid or duplicate source path: {relative}");
            }

            string expected = identity.GetProperty("sha256").GetString()
                ?? throw new InvalidDataException($"Missing source hash: {relative}");
            using FileStream input = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(SHA256.HashData(input));
            if (!StringComparer.Ordinal.Equals(expected, actual))
            {
                throw new InvalidDataException($"Pinned identity changed: {relative}; expected {expected}, observed {actual}.");
            }
            count++;
        }
        return count;
    }
}
