#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../../scripts/ci/script-process.helpers.cs

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var seen = new HashSet<string>(StringComparer.Ordinal);
try
{
    var entries = args[0].Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    if (entries.Length > 4) throw new InvalidOperationException("extlib takes at most one entry per guest");
    var destination = Path.GetFullPath(args[1]);
    foreach (var entry in entries)
    {
        var assignment = entry.Split('=', 2);
        if (assignment.Length != 2 || assignment[0] is not ("zisk" or "sp1" or "openvm" or "airbender") || assignment[1].Length == 0)
            throw new InvalidOperationException("extlib entries must be <guest>=<source>");
        var guest = assignment[0];
        var source = assignment[1];
        if (!seen.Add(guest)) throw new InvalidOperationException($"extlib names {guest} twice");
        var repo = "NethermindEth/" + (guest == "zisk" ? "bflat-libziskos" : "bflat-" + guest);
        var fetched = Directory.CreateDirectory(Path.Combine(destination, guest, "fetched")).FullName;
        var unpacked = Directory.CreateDirectory(Path.Combine(destination, guest, "unpacked")).FullName;

        if (Regex.IsMatch(source, @"\Arun:[0-9]{1,20}\z"))
        {
            var run = source[4..];
            var workflow = guest == "airbender" ? ".github/workflows/ci.yml" : ".github/workflows/build-release.yml";
            var artifact = guest == "airbender" ? "libairbender-riscv32" : "nupkg-unsigned";
            using var response = JsonDocument.Parse(await Require("gh", ["api", $"repos/{repo}/actions/runs/{run}"]));
            var locator = response.RootElement;
            if (locator.GetProperty("head_repository").GetProperty("full_name").GetString() != repo)
                throw new InvalidOperationException($"run {run} of {repo} was not built from {repo}");
            if (locator.GetProperty("path").GetString()!.Split('@', 2)[0] != workflow)
                throw new InvalidOperationException($"run {run} of {repo} is not {workflow}");
            if (locator.GetProperty("conclusion").GetString() != "success")
                throw new InvalidOperationException($"run {run} of {repo} did not succeed");
            Console.Error.Write(await Require("gh", ["run", "download", run, "-R", repo, "-n", artifact, "-D", fetched]));
        }
        else
        {
            var pattern = @"\Ahttps://github\.com/" + Regex.Escape(repo)
                + @"/releases/download/[A-Za-z0-9._+-]{1,128}/[A-Za-z0-9._+-]{1,128}\.(nupkg|tar\.gz(#[A-Za-z0-9._/-]{1,200})?)\z";
            if (!Regex.IsMatch(source, pattern))
                throw new InvalidOperationException($"extlib {guest} must be run:<id> or a release asset URL of {repo}");
            var url = source.Split('#', 2)[0];
            await Require("curl", ["--retry", "3", "-fsSL", url, "-o", Path.Combine(fetched, url[(url.LastIndexOf('/') + 1)..])]);
        }

        var files = Directory.GetFiles(fetched, "*", SearchOption.AllDirectories);
        foreach (var file in files.Where(file => file.EndsWith(".nupkg", StringComparison.Ordinal)))
            await Require("unzip", ["-q", "-o", "-d", unpacked, file]);
        foreach (var file in files.Where(file => file.EndsWith(".tar.gz", StringComparison.Ordinal)))
            await Require("tar", ["-xzf", file, "--no-same-owner", "-C", unpacked]);
        foreach (var file in files.Where(file => !file.EndsWith(".nupkg", StringComparison.Ordinal) && !file.EndsWith(".tar.gz", StringComparison.Ordinal)))
            File.Copy(file, Path.Combine(unpacked, Path.GetFileName(file)), overwrite: true);

        var manifests = guest == "airbender"
            ? Directory.GetFiles(unpacked, "libairbender.bflat.manifest", SearchOption.AllDirectories)
            : Directory.GetFiles(unpacked, "lib*.bflat.manifest", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(Path.GetDirectoryName(file)) == "native")
                // The OpenVM package also ships the runtime of its RV32 line; these guests are RV64.
                .Where(file => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))) != "linux-riscv32")
                .ToArray();
        if (manifests.Length != 1)
            throw new InvalidOperationException($"expected one runtime manifest in the {guest} package, found {manifests.Length}");

        var relativeFiles = files.Select(file => "./" + Path.GetRelativePath(fetched, file)).Order(StringComparer.Ordinal);
        var hashes = await Require("sha256sum", relativeFiles.ToArray(), fetched);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashes)))[..40];
        Console.WriteLine($"{guest} {key} {Path.GetRelativePath(destination, manifests[0])}");
    }
    return 0;
}
catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException)
{
    Console.Error.WriteLine($"::error::{exception.Message}");
    return 1;
}

static async Task<string> Require(string name, string[] arguments, string? directory = null)
{
    var result = await ScriptProcess.Run(name, arguments, CancellationToken.None, workingDirectory: directory);
    Console.Error.Write(result.Error);
    return result.Code == 0 ? result.Output : throw new InvalidOperationException($"{name} failed with exit code {result.Code}");
}
