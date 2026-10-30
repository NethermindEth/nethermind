// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Prove.V2;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class StreamingOptionsTests
{
    [Test]
    public async Task Build_StreamingConfigAbsent_DoesNotExposeV2()
    {
        await using AttesterServer server = await AttesterServer.Start(StatelessFixtures.Window84);
        using AsyncDuplexStreamingCall<ClientFrame, ServerFrame> call = new Prover.ProverClient(server.Channel).ProveStream();
        RpcException exception = (await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await call.RequestStream.WriteAsync(new ClientFrame { Begin = new Begin { RollupId = 1, AnchorHash = ByteString.CopyFrom(new byte[32]), AnchorStateRoot = ByteString.CopyFrom(new byte[32]) } });
            await call.ResponseStream.MoveNext();
        }))!;
        Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.Unimplemented), "v2 activation requires explicit operator configuration");
    }

    [Test]
    public void Load_ValidConfig_ResolvesTrustedPathsAndFiniteLimits()
    {
        using TempPath file = Config(Valid());
        StreamingOptions options = StreamingOptions.Load(file.Path, Attester());
        using (Assert.EnterMultipleScope())
        {
            Assert.That((options.L1ChainId, options.Registry, options.Manager), Is.EqualTo((31337UL, TestItem.AddressA, TestItem.AddressB)), "deployment identity comes from startup configuration");
            Assert.That(options.Profile, Is.EqualTo(StreamingSettlementProfile.Rollup0Calldata));
            Assert.That(Path.IsPathFullyQualified(options.ServerCertificate), Is.True, "certificate paths are anchored to the config directory");
            Assert.That((options.TransportLimits.MaxConnections, options.SessionLimits.MaxBytes, options.RegistryLimits.MaxReservedBytes),
                Is.EqualTo((8, 64L * 1024 * 1024, 512L * 1024 * 1024)), "default resource budgets are finite");
        }
    }

    [Test]
    public void Load_ConditionalProfile_DoesNotRequireUnusedAuthorizationSources()
    {
        JsonObject json = Conditional();
        using TempPath file = Config(json);
        StreamingOptions options = StreamingOptions.Load(file.Path, Attester());
        Assert.That((options.Profile, options.L1Rpc, options.L2Rpc, options.Registry, options.Manager, options.L1ChainId),
            Is.EqualTo((StreamingSettlementProfile.Rollup0Conditional, (Uri?)null, (Uri?)null, (Address?)null, (Address?)null, 0UL)),
            "conditional proofs bind the selected parent without asserting prior L1 acceptance");
    }

    [TestCase("l1Rpc", TestName = "Load_ConditionalWithL1Rpc_RejectsUnusedTrustSetting")]
    [TestCase("l2Rpc", TestName = "Load_ConditionalWithL2Rpc_RejectsUnusedTrustSetting")]
    [TestCase("l1ChainId", TestName = "Load_ConditionalWithL1Chain_RejectsUnusedTrustSetting")]
    [TestCase("registry", TestName = "Load_ConditionalWithRegistry_RejectsUnusedTrustSetting")]
    [TestCase("manager", TestName = "Load_ConditionalWithManager_RejectsUnusedTrustSetting")]
    public void Load_ConditionalWithAuthorizationSetting_Rejects(string name)
    {
        JsonObject json = Conditional();
        json[name] = Valid()[name]!.DeepClone();
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(),
            "unused source settings cannot falsely suggest that the conditional profile authenticates L1 acceptance");
    }

    [Test]
    public void Load_RollupX_ResolvesTrustedProfileInputs([Values("l1.json", "chiado")] string source)
    {
        JsonObject json = Valid();
        json["profile"] = "rollupx-native";
        json["l1ChainConfigPath"] = source;
        json["l2GenesisHash"] = TestItem.KeccakA.ToString();
        using TempPath file = Config(json);
        StreamingOptions options = StreamingOptions.Load(file.Path, Attester());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Profile, Is.EqualTo(StreamingSettlementProfile.RollupXNative));
            Assert.That(options.L1ChainConfigPath, Is.EqualTo(source == "chiado" ? source : Path.GetFullPath(source, Path.GetDirectoryName(file.Path)!)));
            Assert.That(options.L2GenesisHash, Is.EqualTo(TestItem.KeccakA));
        }
    }

    [TestCase("l1ChainConfigPath", null)]
    [TestCase("l1ChainConfigPath", "")]
    [TestCase("l2GenesisHash", null)]
    [TestCase("l2GenesisHash", "0x00")]
    [TestCase("l2GenesisHash", "0x0000000000000000000000000000000000000000000000000000000000000000")]
    public void Load_RollupX_MissingOrInvalidTrustInput_Rejects(string key, string? value)
    {
        JsonObject json = Valid();
        json["profile"] = "rollupx-native";
        json["l1ChainConfigPath"] = "l1.json";
        json["l2GenesisHash"] = TestItem.KeccakA.ToString();
        json[key] = value;
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>());
    }

    [TestCase("l1ChainConfigPath", "l1.json")]
    [TestCase("l2GenesisHash", "0x0000000000000000000000000000000000000000000000000000000000000001")]
    public void Load_Rollup0_WithNativeTrustInput_Rejects(string key, string value)
    {
        JsonObject json = Valid();
        json[key] = value;
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Load_ReservationCoversRetainedAndActiveWindows([Values] bool conditional, [Values] bool belowLimit)
    {
        JsonObject json = conditional ? Conditional() : Valid();
        long reservation = (conditional ? 8L : 4L) * 64 * 1024 * 1024;
        json["maxReservedBytes"] = reservation - (belowLimit ? 1 : 0);
        using TempPath file = Config(json);
        if (belowLimit) Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>());
        else Assert.That(StreamingOptions.Load(file.Path, Attester()).RegistryLimits.MaxReservedBytes, Is.EqualTo(reservation));
    }

    [TestCase("profile", "rollupx", TestName = "Load_UnsupportedProfile_Rejects")]
    [TestCase("registry", "0x0000000000000000000000000000000000000000", TestName = "Load_ZeroRegistry_Rejects")]
    [TestCase("manager", "0x0000000000000000000000000000000000000000", TestName = "Load_ZeroManager_Rejects")]
    [TestCase("l1Rpc", "file:///private/key", TestName = "Load_NonHttpSource_Rejects")]
    [TestCase("l2Rpc", "http://user:private@host", TestName = "Load_SourceUserInfo_Rejects")]
    [TestCase("serverPasswordFile", "", TestName = "Load_MissingPasswordFile_Rejects")]
    [TestCase("listenAddress", "not-an-endpoint", TestName = "Load_InvalidListen_Rejects")]
    public void Load_InvalidTextSetting_RejectsWithoutEchoingValues(string key, string value)
    {
        JsonObject json = Valid();
        json[key] = value;
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>().With.Message.EqualTo("invalid streaming configuration"),
            "config errors cannot echo passwords or source credentials");
    }

    [TestCase("l1ChainId", 0, TestName = "Load_ZeroL1Chain_Rejects")]
    [TestCase("maxConnections", 0, TestName = "Load_ZeroConnections_Rejects")]
    [TestCase("maxConnections", 65, TestName = "Load_ExcessConnections_Rejects")]
    [TestCase("maxSessions", 0, TestName = "Load_ZeroSessions_Rejects")]
    [TestCase("maxSessionsPerOwner", 9, TestName = "Load_ExcessOwnerSessions_Rejects")]
    [TestCase("maxSessionBytes", 0, TestName = "Load_ZeroSessionBytes_Rejects")]
    [TestCase("maxReservedBytes", 100, TestName = "Load_InsufficientByteBudget_Rejects")]
    [TestCase("retentionSeconds", 0, TestName = "Load_ZeroRetention_Rejects")]
    public void Load_InvalidLimit_Rejects(string key, long value)
    {
        JsonObject json = Valid();
        json[key] = value;
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(), "all ingress and retained resources require valid finite budgets");
    }

    [TestCase("profile", TestName = "Load_MissingProfile_Rejects")]
    [TestCase("l1Rpc", TestName = "Load_MissingL1Source_Rejects")]
    [TestCase("l2Rpc", TestName = "Load_MissingL2Source_Rejects")]
    [TestCase("manager", TestName = "Load_MissingManager_Rejects")]
    [TestCase("composerCertificates", TestName = "Load_MissingPins_Rejects")]
    public void Load_PartialConfiguration_Rejects(string key)
    {
        JsonObject json = Valid();
        json.Remove(key);
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(), "partial configuration cannot silently enable an incomplete signer");
    }

    [Test]
    public void Load_PublicV1WhenV2Enabled_Rejects()
    {
        using TempPath file = Config(Valid());
        Assert.That(() => StreamingOptions.Load(file.Path, Attester() with { ListenAddress = new IPEndPoint(IPAddress.Any, 50061) }), Throws.TypeOf<FormatException>(),
            "unauthenticated v1 must not be publicly reachable alongside signed v2");
    }

    [Test]
    public void Load_UnknownProperty_RejectsInsteadOfIgnoringTypo()
    {
        JsonObject json = Valid();
        json["composerCertificates"] = "typo";
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(), "unknown settings must not weaken the intended trust configuration");
    }

    [Test]
    public void Load_OverlargeConfig_RejectsAtByteCap()
    {
        using TempPath file = Config(Valid());
        File.AppendAllText(file.Path, new string(' ', 64 * 1024));
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(), "even valid padded JSON cannot bypass the byte cap");
    }

    [Test]
    public async Task StopAndDrain_HeldBackendLease_WaitsForWorkerCleanupAfterAppDisposal()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        BackendAdmission admission = new(NullLogger<BackendAdmission>.Instance);
        using BackendAdmission.BackendLease slot = admission.TryAcquire()!;
        TaskCompletionSource worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        Task release = slot.ReleaseAfter(worker.Task, () => cleaned = true);
        Task stop = Program.StopAndDrain(app, admission);
        try
        {
            Assert.That(() => app.Services.GetService<object>(), Throws.TypeOf<ObjectDisposedException>(), "app disposal must precede the final backend drain");
            Assert.That(stop.IsCompleted, Is.False, "shutdown cannot complete while the controlled worker still holds admission");
            worker.SetResult();
            await stop;
            Assert.That(cleaned, Is.True, "runtime disposal is permitted only after worker cleanup releases admission");
        }
        finally
        {
            worker.TrySetResult();
            await release;
            await stop;
        }
    }

    [TestCase(0, TestName = "Load_EmptyComposerPins_Rejects")]
    [TestCase(17, TestName = "Load_ExcessComposerPins_Rejects")]
    public void Load_EmptyOrExcessComposerPins_Rejects(int count)
    {
        JsonObject json = Valid();
        JsonArray pins = [];
        for (int i = 0; i < count; i++) pins.Add("composer.cer");
        json["composerCertificates"] = pins;
        using TempPath file = Config(json);
        Assert.That(() => StreamingOptions.Load(file.Path, Attester()), Throws.TypeOf<FormatException>(), "pin count must be nonzero and bounded");
    }

    [TestCase("missing-server", TestName = "Runtime_MissingServer_Rejects")]
    [TestCase("invalid-server", TestName = "Runtime_InvalidServer_Rejects")]
    [TestCase("wrong-password", TestName = "Runtime_WrongPassword_Rejects")]
    [TestCase("no-private-key", TestName = "Runtime_ServerWithoutPrivateKey_Rejects")]
    [TestCase("expired-server", TestName = "Runtime_ExpiredServer_Rejects")]
    [TestCase("future-server", TestName = "Runtime_FutureServer_Rejects")]
    [TestCase("missing-pin", TestName = "Runtime_MissingComposerPin_Rejects")]
    [TestCase("invalid-pin", TestName = "Runtime_InvalidComposerPin_Rejects")]
    [TestCase("expired-pin", TestName = "Runtime_ExpiredComposerPin_Rejects")]
    [TestCase("oversize-server", TestName = "Runtime_OversizeServerFile_Rejects")]
    [TestCase("oversize-password", TestName = "Runtime_OversizePasswordFile_Rejects")]
    [TestCase("oversize-pin", TestName = "Runtime_OversizeComposerPinFile_Rejects")]
    public void Runtime_InvalidCertificateFiles_RejectsBeforeListening(string mutation)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string serverPath = Path.Combine(directory.Path, "server.pfx");
        string passwordPath = Path.Combine(directory.Path, "password");
        string pinPath = Path.Combine(directory.Path, "composer.cer");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 server = Certificate(mutation == "expired-server" ? now.AddDays(-2) : mutation == "future-server" ? now.AddDays(1) : now.AddDays(-1),
            mutation == "expired-server" ? now.AddDays(-1) : now.AddDays(2));
        using X509Certificate2 pin = Certificate(now.AddDays(-2), mutation == "expired-pin" ? now.AddDays(-1) : now.AddDays(2));
        if (mutation == "no-private-key")
        {
            using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(server.RawData);
            File.WriteAllBytes(serverPath, publicOnly.Export(X509ContentType.Pkcs12, "test"));
        }
        else File.WriteAllBytes(serverPath, server.Export(X509ContentType.Pkcs12, "test"));
        File.WriteAllText(passwordPath, mutation == "wrong-password" ? "private password must not be echoed" : "test");
        File.WriteAllBytes(pinPath, pin.RawData);
        switch (mutation)
        {
            case "missing-server": File.Delete(serverPath); break;
            case "invalid-server": File.WriteAllText(serverPath, "invalid certificate"); break;
            case "missing-pin": File.Delete(pinPath); break;
            case "invalid-pin": File.WriteAllText(pinPath, "invalid certificate"); break;
            case "oversize-server": File.WriteAllBytes(serverPath, new byte[1024 * 1024 + 1]); break;
            case "oversize-password": File.WriteAllText(passwordPath, new string('x', 4097)); break;
            case "oversize-pin": File.WriteAllBytes(pinPath, new byte[64 * 1024 + 1]); break;
        }
        JsonObject json = Valid();
        json["serverCertificate"] = serverPath;
        json["serverPasswordFile"] = passwordPath;
        json["composerCertificates"] = new JsonArray(pinPath);
        using TempPath config = Config(json);
        ISpecProvider specs = StatelessFixtures.ReadSpecProvider(StatelessFixtures.Window84, "chain-config.json");
        AttesterOptions options = Attester() with { StreamingConfigPath = config.Path };
        EezSettlementContext context = new(options.RollupId, specs.ChainId, options.ProofSystem, options.VerificationKey, options.BlockTimeSeconds);
        EezAttestationSigner signer = new(AttesterServer.AttesterKey);
        AttestationPipeline pipeline = new(specs, context, signer, LimboLogs.Instance);
        Assert.That(() => StreamingRuntime.Load(options, specs, pipeline, signer, LimboLogs.Instance),
            Throws.TypeOf<FormatException>().With.Message.EqualTo("invalid streaming configuration"), "startup cannot expose certificate paths or passwords in errors");
    }

    private static X509Certificate2 Certificate(DateTimeOffset from, DateTimeOffset to)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(from, to);
    }

    private static AttesterOptions Attester() => new(new IPEndPoint(IPAddress.Loopback, 50061), "", 1, TestItem.KeccakA.ValueHash256,
        TestItem.AddressA, TestItem.AddressB, "", "", 2, 30_000_000, WindowLimits.Default, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(600));

    private static JsonObject Valid() => new()
    {
        ["profile"] = "rollup0-calldata",
        ["listenAddress"] = "127.0.0.1:50063",
        ["l1Rpc"] = "http://l1.invalid",
        ["l2Rpc"] = "http://l2.invalid",
        ["l1ChainId"] = 31337,
        ["registry"] = TestItem.AddressA.ToString(),
        ["manager"] = TestItem.AddressB.ToString(),
        ["serverCertificate"] = "server.pfx",
        ["serverPasswordFile"] = "password",
        ["composerCertificates"] = new JsonArray("composer.cer"),
    };

    private static JsonObject Conditional()
    {
        JsonObject json = Valid();
        json["profile"] = "rollup0-conditional";
        foreach (string key in new[] { "l1Rpc", "l2Rpc", "l1ChainId", "registry", "manager" }) json.Remove(key);
        return json;
    }

    private static TempPath Config(JsonObject json)
    {
        TempPath file = TempPath.GetTempFile();
        File.WriteAllText(file.Path, json.ToJsonString());
        return file;
    }
}
