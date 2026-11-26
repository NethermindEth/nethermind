#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../../scripts/ci/script-process.helpers.cs

// Validates the A/B inputs, resolves both refs to commits on a NethermindEth/nethermind branch,
// fetches the candidate packages and writes the job matrices and cache keys to GITHUB_OUTPUT.
// CANDIDATE_ARM=both gives the image, extlib and codegenopt inputs to both arms; head gives them
// to the head arm only, so that base and head at the same commit measure the candidate alone.
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

const string RepoUrl = "https://github.com/NethermindEth/nethermind.git";
string[] allGuests = ["zisk", "sp1", "openvm", "airbender"];
var projects = new Dictionary<string, string>
{
    ["zisk"] = "Nethermind.Stateless.ZiskGuest",
    ["sp1"] = "Nethermind.Stateless.Sp1Guest",
    ["openvm"] = "Nethermind.Stateless.OpenVMGuest",
    ["airbender"] = "Nethermind.Stateless.AirbenderGuest",
};

try
{
    // A line break in an input could add lines to GITHUB_OUTPUT
    foreach (var name in new[] { "BASE_REF", "HEAD_REF", "BFLAT_IMAGE", "BFLAT_RISCV32_IMAGE", "EXTLIB", "CODEGENOPT", "GUESTS", "FORMATS", "CANDIDATE_ARM" })
    {
        if (Env(name).AsSpan().IndexOfAny('\n', '\r') >= 0)
            throw new InvalidOperationException($"{name.ToLowerInvariant()} must not contain a line break");
    }

    var baseRef = Env("BASE_REF") is { Length: > 0 } b ? b : "master";
    var headRef = Env("HEAD_REF");
    var guests = Env("GUESTS") is { Length: > 0 } g ? g : "zisk,sp1,openvm,airbender";
    var formats = Env("FORMATS") is { Length: > 0 } f ? f : "ssz,trusted";
    var candidateArm = Env("CANDIDATE_ARM") is { Length: > 0 } c ? c : "both";
    var image = Env("BFLAT_IMAGE");
    var riscv32Image = Env("BFLAT_RISCV32_IMAGE");
    var extlib = Env("EXTLIB");
    if (candidateArm is not ("both" or "head"))
        throw new InvalidOperationException("candidate_arm must be both or head");

    CheckRef("base_ref", baseRef);
    CheckRef("head_ref", headRef);
    foreach (var (name, value) in new[] { ("bflat_image", image), ("bflat_riscv32_image", riscv32Image) })
    {
        if (value.Length > 0 && !ImageRegex().IsMatch(value))
            throw new InvalidOperationException($"{name} must be an image pinned by digest (name[:tag]@sha256:...)");
    }

    var opts = Items(Env("CODEGENOPT"));
    if (opts.Any(opt => !OptRegex().IsMatch(opt)))
        throw new InvalidOperationException("codegenopt entries must be Name=Value");
    if (opts.Length > 8)
        throw new InvalidOperationException("codegenopt takes at most 8 entries");
    var flags = string.Join(' ', opts.Select(opt => $"--codegenopt '{opt}'"));

    var wanted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var guest in Items(guests))
    {
        if (!allGuests.Contains(guest))
            throw new InvalidOperationException("guests entries must be zisk, sp1, openvm or airbender");
        wanted.Add(guest);
    }
    var selectedGuests = allGuests.Where(wanted.Contains).ToArray();
    if (selectedGuests.Length == 0)
        throw new InvalidOperationException("guests is empty");

    var selectedFormats = new List<string>();
    foreach (var format in Items(formats))
    {
        var name = format.ToLowerInvariant() switch
        {
            "ssz" => "Ssz",
            "trusted" => "Trusted",
            _ => throw new InvalidOperationException("formats entries must be ssz or trusted"),
        };
        if (!selectedFormats.Contains(name))
            selectedFormats.Add(name);
    }
    if (selectedFormats.Count == 0)
        throw new InvalidOperationException("formats is empty");

    var refs = Path.Combine(Env("RUNNER_TEMP"), "refs");
    await Git("init", "-q", refs);
    await Git("-C", refs, "remote", "add", "origin", RepoUrl);
    var heads = (await Git("-C", refs, "ls-remote", "--heads", "origin"))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t'))
        .Where(parts => parts.Length == 2)
        .ToArray();
    var fetched = false;
    var baseSha = await Resolve(baseRef);
    var headSha = await Resolve(headRef);
    Console.WriteLine($"base {baseRef} -> {baseSha}");
    Console.WriteLine($"head {headRef} -> {headSha}");

    var extlibs = new Dictionary<string, (string Key, string Path)>();
    if (extlib.Replace(" ", "").Replace(",", "").Length > 0)
    {
        var fetch = await ScriptProcess.Run("dotnet", ["run", "--file", ".github/zkvm-ab/fetch-extlib.cs", "--", extlib, Path.Combine(Env("RUNNER_TEMP"), "candidate")], CancellationToken.None);
        Console.Error.Write(fetch.Error);
        if (fetch.Code != 0)
            return fetch.Code;
        foreach (var line in fetch.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 3);
            extlibs[parts[0]] = (parts[1], parts[2]);
        }
    }

    var formatList = string.Join(',', selectedFormats);
    var keys = new JsonObject();
    var builds = new Dictionary<string, JsonArray> { ["base"] = [], ["head"] = [] };
    var measure = new JsonArray();
    foreach (var guest in selectedGuests)
    {
        var guestImage = guest == "airbender" ? riscv32Image : image;
        foreach (var arm in new[] { "base", "head" })
        {
            var candidate = arm == "head" || candidateArm == "both";
            var armImage = candidate ? guestImage : "";
            var armFlags = candidate ? flags : "";
            var (extlibKey, extlibPath) = candidate && extlibs.TryGetValue(guest, out var entry) ? entry : ("", "");
            // The base binaries and results are cached under the settings that the base arm really builds with
            if (arm == "base")
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"base={baseSha}\nimage={armImage}\nflags={armFlags}\nextlib={extlibKey}\nformats={formatList}\n"));
                keys[guest] = Convert.ToHexStringLower(hash)[..40];
            }
            builds[arm].Add(new JsonObject
            {
                ["vm"] = guest,
                ["project"] = projects[guest],
                ["arm"] = arm,
                ["sha"] = arm == "base" ? baseSha : headSha,
                ["image"] = armImage,
                ["flags"] = armFlags,
                ["extlib"] = extlibPath,
            });
        }
        foreach (var format in selectedFormats)
            measure.Add(new JsonObject { ["vm"] = guest, ["project"] = projects[guest], ["format"] = format });
    }

    var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.AppendAllText(Env("GITHUB_OUTPUT"), string.Join("", new[]
    {
        $"base_sha={baseSha}",
        $"head_sha={headSha}",
        $"formats={formatList}",
        $"guests={string.Join(',', selectedGuests)}",
        $"candidate_arm={candidateArm}",
        $"keys={keys.ToJsonString(json)}",
        $"candidate={(extlibs.Count > 0 ? "true" : "false")}",
        $"openvm={(wanted.Contains("openvm") ? "true" : "false")}",
        $"base_build_matrix={new JsonObject { ["include"] = builds["base"] }.ToJsonString(json)}",
        $"head_build_matrix={new JsonObject { ["include"] = builds["head"] }.ToJsonString(json)}",
        $"measure_matrix={new JsonObject { ["include"] = measure }.ToJsonString(json)}",
    }.Select(line => line + "\n")));
    return 0;

    async Task<string> Resolve(string reference)
    {
        if (!ShaRegex().IsMatch(reference))
        {
            return heads.FirstOrDefault(parts => parts[1] == $"refs/heads/{reference}")?[0]
                ?? throw new InvalidOperationException($"{reference} is not a branch of NethermindEth/nethermind");
        }
        if (heads.Any(parts => parts[0] == reference))
            return reference;
        if (!fetched)
        {
            await Git("-C", refs, "fetch", "-q", "--filter=tree:0", "--no-tags", "origin", "+refs/heads/*:refs/remotes/origin/*");
            fetched = true;
        }
        Environment.SetEnvironmentVariable("GIT_NO_LAZY_FETCH", "1");
        var commit = await ScriptProcess.Run("git", ["-C", refs, "cat-file", "-e", $"{reference}^{{commit}}"], CancellationToken.None);
        var containing = commit.Code == 0
            ? await ScriptProcess.Run("git", ["-C", refs, "for-each-ref", "--contains", reference, "--count=1", "refs/remotes/origin"], CancellationToken.None)
            : commit;
        return commit.Code == 0 && containing.Code == 0 && containing.Output.Trim().Length > 0
            ? reference
            : throw new InvalidOperationException($"{reference} is not on a branch of NethermindEth/nethermind");
    }
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine($"::error::{exception.Message}");
    return 1;
}

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static string[] Items(string list) => list.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

static void CheckRef(string name, string value)
{
    if (ShaRegex().IsMatch(value))
        return;
    if (!RefRegex().IsMatch(value) || value.Contains("..", StringComparison.Ordinal) || value.StartsWith('-') || value.StartsWith('/') || value.EndsWith('/'))
        throw new InvalidOperationException($"{name} must be a branch name or a full commit SHA");
}

static async Task<string> Git(params string[] arguments)
{
    var result = await ScriptProcess.Run("git", arguments, CancellationToken.None);
    Console.Error.Write(result.Error);
    return result.Code == 0 ? result.Output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {result.Code}");
}

partial class Program
{
    [GeneratedRegex(@"\A[0-9a-f]{40}\z")]
    private static partial Regex ShaRegex();
    [GeneratedRegex(@"\A[A-Za-z0-9._/-]{1,200}\z")]
    private static partial Regex RefRegex();
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9._/-]{0,199}(:[A-Za-z0-9._-]{1,128})?@sha256:[0-9a-f]{64}\z")]
    private static partial Regex ImageRegex();
    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,63}=[A-Za-z0-9._:+-]{1,64}\z")]
    private static partial Regex OptRegex();
}
