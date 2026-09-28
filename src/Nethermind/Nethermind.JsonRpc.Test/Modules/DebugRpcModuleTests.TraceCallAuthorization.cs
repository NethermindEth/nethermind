// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    [Test]
    public async Task Debug_traceCall_prestate_captures_authorizations_before_execution(
        [Values("grant", "revoke", "wrongChain", "wrongNonce", "maxNonce", "highS", "badV", "duplicate", "sender")] string scenario,
        [Values] bool diffMode,
        [Values] bool disableCode,
        [Values(null, "0x0")] string? txIndex)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Prague.Instance));
        bool senderAuthority = scenario == "sender";
        Address authority = senderAuthority ? TestItem.AddressA : TestItem.AddressE;
        PrivateKey signer = senderAuthority ? TestItem.PrivateKeyA : TestItem.PrivateKeyE;
        ulong nonce = scenario switch { "wrongNonce" or "sender" => 1, "maxNonce" => ulong.MaxValue, _ => 0 };
        ulong chainId = scenario == "wrongChain" ? 999999ul : 0;
        Address target = scenario == "revoke" ? Address.Zero : TestItem.AddressF;
        EthereumEcdsa ecdsa = new(1);
        AuthorizationTuple authorization = ecdsa.Sign(signer, chainId, target, nonce);
        object tuple = AuthorizationForTrace(authorization, scenario);
        object[] authorizationList = scenario == "duplicate" ? [tuple, tuple] : [tuple];
        string delegation = "0xef0100" + TestItem.AddressF.ToString()[2..];
        Dictionary<string, object> stateOverrides = new()
        {
            [TestItem.AddressA.ToString()] = new { balance = "0x1000000000000000000", nonce = "0x0", code = "0x" },
            [TestItem.AddressD.ToString()] = new { code = "0x00" }
        };
        if (!senderAuthority)
            stateOverrides[authority.ToString()] = new { balance = "0x1", nonce = "0x0", code = scenario == "revoke" ? delegation : "0x" };
        string response = await RpcTest.TestSerializedRequest(ctx.DebugRpcModule, "debug_traceCall",
            new { from = TestItem.AddressA.ToString(), to = TestItem.AddressD.ToString(), gas = "0x493e0", authorizationList },
            "latest", new { tracer = "prestateTracer", tracerConfig = new { diffMode, disableCode }, stateOverrides, txIndex });
        JToken json = JToken.Parse(response);
        Assert.That(json["error"], Is.Null, response);
        JToken result = json["result"]!;
        bool signatureValid = scenario is not "highS" and not "badV";
        bool applied = scenario is "grant" or "revoke" or "duplicate" or "sender";
        JToken pre = diffMode ? result["pre"]! : result;
        Assert.That(pre[authority.ToString()] is not null, Is.EqualTo(diffMode ? applied : signatureValid), response);
        if (applied)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pre[authority.ToString()]!["nonce"]?.Value<ulong>() ?? 0, Is.Zero, response);
                Assert.That(pre[authority.ToString()]!["code"]?.Value<string>(), Is.EqualTo(scenario == "revoke" && !disableCode ? delegation : null), response);
                if (diffMode)
                {
                    JToken post = result["post"]![authority.ToString()]!;
                    Assert.That(post["nonce"]!.Value<ulong>(), Is.EqualTo(senderAuthority ? 2ul : 1ul));
                    Assert.That(post["code"]?.Value<string>(), Is.EqualTo(disableCode ? null : scenario == "revoke" ? "0x" : delegation));
                    Assert.That(post["codeHash"], Is.Not.Null);
                }
            }
        }
    }

    private static object AuthorizationForTrace(AuthorizationTuple authorization, string scenario)
    {
        Signature signature = authorization.AuthoritySignature;
        UInt256 r = new(signature.RAsSpan, true);
        UInt256 s = new(signature.SAsSpan, true);
        byte parity = signature.RecoveryId;
        if (scenario == "highS")
        {
            s = SecP256k1Curve.N - s;
            parity ^= 1;
        }
        if (scenario == "badV") parity = 2;
        return new
        {
            chainId = authorization.ChainId, address = authorization.CodeAddress,
            nonce = authorization.Nonce, yParity = (ulong)parity,
            r, s
        };
    }
}
