// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Nethermind.Tools.ProfilingMcp.Memory;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class DebuggingLibrariesTests
{
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", "amd64", ExpectedResult = "linux-x64 10.0.14")]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", "arm64", ExpectedResult = "linux-arm64 10.0.14")]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/11.0.0-preview.3.26/libcoreclr.so", "amd64", ExpectedResult = "linux-x64 11.0.0-preview.3.26")]
    [TestCase("/app/libcoreclr.so", "amd64", ExpectedResult = null)]
    [TestCase("/x/10.0.14;curl evil/libcoreclr.so", "amd64", ExpectedResult = null)]
    [TestCase("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", "s390x", ExpectedResult = null)]
    public string? Tells_the_runtime_pack_from_the_runtime_path(string coreclrPath, string architecture) =>
        DebuggingLibraries.Runtime(coreclrPath, architecture) is var (rid, version) ? $"{rid} {version}" : null;

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
            Assert.That(DebuggingLibraries.Choose("10.0.14", "10.0.12", "/profmcp/libmscordaccore.so", [$"{DebuggingLibraries.SidecarFolder}/linux-musl-{arch}/10.0.14/libmscordaccore.so"]), Is.Null);
        }
    }

    [Test]
    public void Reads_each_target_runtime_and_whether_it_runs_on_musl()
    {
        const string Output = """
            glibc /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so
            musl /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so
            sh: 1: cannot open /proc/7/maps

            """;

        Assert.That(DebuggingLibraries.ParseRuntimes(Output), Is.EqualTo(new[]
        {
            ("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libcoreclr.so", false),
            ("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so", true),
        }));
    }

    [TestCase("""{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}""", ExpectedResult = "10.0.12")]
    [TestCase("""{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""", ExpectedResult = null)]
    [TestCase("not json", ExpectedResult = null)]
    public string? Reads_the_sidecars_own_runtime_version(string runtimeConfig) => DebuggingLibraries.IncludedRuntimeVersion(runtimeConfig);

    [Test]
    public void Picks_its_own_dac_for_a_preview_by_the_version_the_host_compares()
    {
        string server = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(server, "Nethermind.Tools.ProfilingMcp.runtimeconfig.json"),
            """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"11.0.0-rc.2.26502.107"}]}}""");
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(DebuggingLibraries.TrustedDac("/usr/share/dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.2.26502.107/libcoreclr.so", server, Path.Combine(server, "supplied")),
                    Is.EqualTo(Path.Combine(server, "libmscordaccore.so")), "Environment.Version drops the preview's suffix, so it would not match");
                Assert.That(DebuggingLibraries.OwnVersion(Path.Combine(server, "missing.runtimeconfig.json")), Is.EqualTo(Environment.Version.ToString(3)));
            }
        }
        finally
        {
            Directory.Delete(server, recursive: true);
        }
    }

    [Test]
    public void Trusts_the_certificate_microsoft_signs_runtime_packs_with()
    {
        SignedCms signature = RuntimePackSignature();

        Assert.That(() => DebuggingLibraries.CheckSigner(signature.SignerInfos[0].Certificate, signature.Certificates), Throws.Nothing);
    }

    [Test]
    public void Refuses_a_signer_under_another_root([Values(DebuggingLibraries.MicrosoftSigner, "CN=Someone Else")] string subject)
    {
        using RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new("CN=DigiCert Trusted Root G4, OU=www.digicert.com, O=DigiCert Inc, C=US", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using RSA signerKey = RSA.Create(2048);
        CertificateRequest signerRequest = new(subject, signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        signerRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        using X509Certificate2 signer = signerRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(1), [1, 2, 3, 4]);

        Assert.That(() => DebuggingLibraries.CheckSigner(signer, [root, signer]), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void Refuses_a_runtime_pack_that_is_unsigned_or_changed([Values] bool signed)
    {
        using MemoryStream package = new();
        using (ZipArchive zip = new(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (StreamWriter nuspec = new(zip.CreateEntry("microsoft.netcore.app.runtime.linux-x64.nuspec").Open()))
                nuspec.Write("""<package><metadata><id>Microsoft.NETCore.App.Runtime.linux-x64</id><version>11.0.0</version></metadata></package>""");
            using (Stream dac = zip.CreateEntry("runtimes/linux-x64/native/libmscordaccore.so").Open())
                dac.Write([1, 2, 3]);
            if (signed)
            {
                ZipArchiveEntry entry = zip.CreateEntry(".signature.p7s", CompressionLevel.NoCompression);
                // NuGet reads a signature only from an entry without file attributes, as packages are signed.
                entry.ExternalAttributes = 0;
                using Stream file = entry.Open();
                file.Write(RuntimePackSignature().Encode());
            }
        }

        package.Position = 0;

        Assert.That(async () => await DebuggingLibraries.VerifyAsync(package, CancellationToken.None),
            Throws.InstanceOf<InvalidDataException>().With.Message.Contains(signed ? "signature does not check out" : "not signed"));
    }

    [Test]
    public async Task Records_a_download_timeout_as_one_runtimes_problem()
    {
        // HttpClient reports its own timeout as a cancellation, though the caller did not cancel.
        string? problem = await DebuggingLibraries.TrySupplyAsync(
            static () => Task.FromException(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.")),
            CancellationToken.None);

        Assert.That(problem, Does.Contain("HttpClient.Timeout"));
    }

    [Test]
    public void Ends_the_preparation_when_the_caller_cancels()
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();

        Assert.That(async () => await DebuggingLibraries.TrySupplyAsync(() => Task.FromCanceled(caller.Token), caller.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Leaves_no_partial_file_behind(bool fails)
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        string dac = Path.Combine(folder, "libmscordaccore.so");
        try
        {
            using Stream source = fails ? new FailingStream() : new MemoryStream([1, 2, 3]);
            Task saving = DebuggingLibraries.SaveAsync(source, dac, CancellationToken.None);
            if (fails)
                Assert.That(async () => await saving, Throws.InstanceOf<IOException>());
            else
                await saving;

            Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), fails ? Is.Empty : Is.EqualTo(new[] { "libmscordaccore.so" }));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void Refuses_only_a_target_that_loaded_musl()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => DebuggingLibraries.RequireGlibc(["/lib/ld-musl-x86_64.so.1", "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so"]),
                Throws.InvalidOperationException.With.Message.Contains("musl"));
            Assert.That(() => DebuggingLibraries.RequireGlibc(["/lib/x86_64-linux-gnu/libc.so.6", "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so"]),
                Throws.Nothing);
        }
    }

    [TestCase("native_memory", "1", ExpectedResult = true)]
    [TestCase("write_dump", "1", ExpectedResult = true)]
    [TestCase("heap_stats", "/profmcp-home/heap/a.dmp", ExpectedResult = true)]
    [TestCase("heap_retention", "1", ExpectedResult = false)]
    [TestCase("cpu_sample", "1", ExpectedResult = false)]
    public bool Supplies_dacs_only_for_tools_that_load_one(string tool, string target) =>
        DebuggingLibraries.AreNeededBy(tool, new Dictionary<string, JsonElement> { ["target"] = JsonSerializer.SerializeToElement(target) });

    /// <summary>A download that breaks after its first bytes.</summary>
    private static SignedCms RuntimePackSignature()
    {
        using Stream resource = typeof(DebuggingLibrariesTests).Assembly.GetManifestResourceStream("runtime-pack.signature.p7s")!;
        using MemoryStream bytes = new();
        resource.CopyTo(bytes);
        SignedCms signature = new();
        signature.Decode(bytes.ToArray());
        return signature;
    }

    private sealed class FailingStream : MemoryStream
    {
        private bool _started;

        public FailingStream() : base([1, 2, 3]) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_started)
                throw new IOException("connection reset");
            _started = true;
            return base.ReadAsync(buffer[..1], cancellationToken);
        }
    }
}
