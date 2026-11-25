#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../../scripts/ci/script-process.helpers.cs

// Usage: build.cs <guest> <project> <checkout> <out>
// Builds the guest of <checkout> for every format and copies the binaries to <out>.
// Environment: FORMATS, BFLAT_IMAGE (the image of this guest and arm), BFLAT_FLAGS, EXTLIB_PATH,
// CANDIDATE_DIR.
using System.Security.Cryptography;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: build.cs <guest> <project> <checkout> <out>");
    return 2;
}

try
{
    var guest = args[0];
    var checkout = Path.GetFullPath(args[2]);
    if (!Directory.Exists(checkout))
        throw new InvalidOperationException($"{args[2]} does not exist");
    var directory = Path.Combine(checkout, "src/Nethermind", args[1]);
    var output = Path.GetFullPath(args[3]);
    var makeArgs = new List<string>();

    var image = Env("BFLAT_IMAGE");
    var flags = Env("BFLAT_FLAGS");
    var extlib = Env("EXTLIB_PATH");
    foreach (var name in new[] { "BFLAT_IMAGE", "BFLAT_FLAGS", "EXTLIB_PATH" })
        Environment.SetEnvironmentVariable(name, null);
    if (image.Length > 0)
        makeArgs.Add($"BFLAT_IMAGE={image}");

    if (flags.Length > 0)
    {
        var buildMk = Path.Combine(directory, "../Nethermind.Stateless.Guest/build.mk");
        if (!File.Exists(buildMk) || !File.ReadLines(buildMk).Any(line => line.StartsWith("BFLAT_EXTRA_FLAGS ?=", StringComparison.Ordinal)))
            throw new InvalidOperationException($"{args[2]} has no BFLAT_EXTRA_FLAGS in build.mk, so codegenopt cannot reach its guest");
        makeArgs.Add($"BFLAT_EXTRA_FLAGS={flags}");
    }

    if (extlib.Length > 0)
    {
        var candidate = Path.GetFullPath(Env("CANDIDATE_DIR"));
        if (!Directory.Exists(candidate))
            throw new InvalidOperationException($"{Env("CANDIDATE_DIR")} does not exist");
        if (guest == "airbender")
        {
            makeArgs.Add($"AIRBENDER_LIB_DIR={Path.GetDirectoryName($"{candidate}/{extlib}")}");
        }
        else
        {
            makeArgs.Add($"GUEST_EXTLIB=/candidate/{extlib}");
            makeArgs.Add($"BFLAT_DOCKER_ARGS=--mount type=bind,source=\"{candidate}\",target=/candidate,readonly");
        }
    }

    foreach (var format in Env("FORMATS").Split([',', ' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        var make = await ScriptProcess.Run("make", ["-C", directory, "build", $"GUEST_INPUT={format}", .. makeArgs], CancellationToken.None, echo: true, capture: false);
        if (make.Code != 0)
            return make.Code;
    }

    var binaries = Directory.Exists(Path.Combine(directory, "bin")) ? Directory.GetFiles(Path.Combine(directory, "bin"), "nethermind*") : [];
    if (binaries.Length == 0)
        throw new InvalidOperationException($"{directory}/bin has no guest binaries");
    Directory.CreateDirectory(output);
    foreach (var binary in binaries)
        File.Copy(binary, Path.Combine(output, Path.GetFileName(binary)), overwrite: true);
    foreach (var file in Directory.GetFiles(output).Order(StringComparer.Ordinal))
    {
        using var stream = File.OpenRead(file);
        Console.WriteLine($"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {file}");
    }
    return 0;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"::error::{exception.Message}");
    return 1;
}

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";
