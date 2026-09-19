// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.DebugModule;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Migration_rpc_uses_registered_telemetry_and_exact_wire_types([Values] bool enabled)
    {
        IMigrationTelemetry telemetry = Substitute.For<IMigrationTelemetry>();
        telemetry.GetProgress().Returns(new MigrationProgressForRpc("running",
            new("stalled", 17, TestItem.KeccakA, TestItem.KeccakB, "Missing BAL"), null));
        telemetry.GetShadowRoot(TestItem.KeccakA).Returns(TestItem.KeccakB);
        using TestRpcBlockchain blockchain = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(builder =>
        {
            if (enabled) builder.AddSingleton(telemetry);
        });
        IDebugRpcModule module = blockchain.DebugRpcModule;
        using JsonDocument progress = JsonDocument.Parse(await RpcTest.TestSerializedRequest(module, "debug_migrationProgress"));
        JsonElement result = progress.RootElement.GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("phase").GetString(), Is.EqualTo(enabled ? "running" : "inactive"));
            Assert.That(result.GetProperty("merkle").ValueKind, Is.EqualTo(JsonValueKind.Null));
            if (enabled)
            {
                JsonElement binary = result.GetProperty("binary");
                Assert.That(binary.GetProperty("cursor").GetUInt64(), Is.EqualTo(17));
                Assert.That(binary.GetProperty("cursorHash").GetString(), Is.EqualTo(TestItem.KeccakA.ToString()));
                Assert.That(binary.GetProperty("shadowRoot").GetString(), Is.EqualTo(TestItem.KeccakB.ToString()));
                Assert.That(binary.GetProperty("error").GetString(), Is.EqualTo("Missing BAL"));
            }
            else Assert.That(result.GetProperty("binary").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        foreach (Hash256 hash in new[] { TestItem.KeccakA, TestItem.KeccakC })
        {
            using JsonDocument shadow = JsonDocument.Parse(await RpcTest.TestSerializedRequest(module, "debug_shadowStateRoot", hash));
            JsonElement root = shadow.RootElement.GetProperty("result");
            Assert.That(root.GetString(), Is.EqualTo(enabled && hash == TestItem.KeccakA ? TestItem.KeccakB.ToString() : null));
        }
    }
}
