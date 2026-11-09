// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Tools.ProfilingMcp.Memory;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class DebuggingLibrariesTests
{
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", false, "amd64", ExpectedResult = "linux-x64 10.0.14")]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", true, "arm64", ExpectedResult = "linux-musl-arm64 10.0.14")]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/11.0.0-preview.3.26/libcoreclr.so", false, "amd64", ExpectedResult = "linux-x64 11.0.0-preview.3.26")]
    [TestCase("/app/libcoreclr.so", false, "amd64", ExpectedResult = null)]
    [TestCase("/x/10.0.14;curl evil/libcoreclr.so", false, "amd64", ExpectedResult = null)]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", false, "s390x", ExpectedResult = null)]
    public string? Tells_the_runtime_pack_from_the_runtime_path(string coreclrPath, bool musl, string architecture) =>
        DebuggingLibraries.Runtime(coreclrPath, musl, architecture) is var (rid, version) ? $"{rid} {version}" : null;

    [Test]
    public void Loads_its_own_or_a_supplied_dac_and_never_the_targets()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string supplied = $"{DebuggingLibraries.SidecarFolder}/linux-{arch}/10.0.14/libmscordaccore.so";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(DebuggingLibraries.Choose("10.0.12", "10.0.12", "/profmcp/libmscordaccore.so", [supplied]), Is.EqualTo("/profmcp/libmscordaccore.so"));
            Assert.That(DebuggingLibraries.Choose("10.0.14", "10.0.12", "/profmcp/libmscordaccore.so", [supplied]), Is.EqualTo(supplied));
            Assert.That(DebuggingLibraries.Choose("10.0.13", "10.0.12", "/profmcp/libmscordaccore.so", [supplied]), Is.Null);
        }
    }

    [TestCase("native_memory", "1", ExpectedResult = true)]
    [TestCase("heap_stats", "/profmcp-home/heap/a.dmp", ExpectedResult = true)]
    [TestCase("heap_retention", "1", ExpectedResult = false)]
    [TestCase("cpu_sample", "1", ExpectedResult = false)]
    public bool Supplies_dacs_only_for_tools_that_load_one(string tool, string target) =>
        DebuggingLibraries.AreNeededBy(tool, new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement(target) });
}
