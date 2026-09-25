// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Engine;

public class EngineDriverForkchoiceVersionTests
{
    /// <remarks>
    /// execution-apis amsterdam.md ("Update the methods of previous forks", Osaka API) rejects
    /// engine_forkchoiceUpdatedV3 only for a payload timestamp at or after Amsterdam, and paris.md
    /// point 8 runs payload-attribute checks only when attributes are provided. So V3 stays valid
    /// after Amsterdam only while the driver never sends attributes.
    /// </remarks>
    [Test]
    public async Task Forkchoice_update_is_v3_without_payload_attributes()
    {
        Hash256 head = TestItem.KeccakA;
        Hash256 safe = TestItem.KeccakB;
        Hash256 finalized = TestItem.KeccakC;
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        inner.engine_forkchoiceUpdatedV3(default!, default)
            .ReturnsForAnyArgs(Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, head)));
        ExternalClDetector detector = new(new BeaconChainConfig { Enabled = true }, new Lazy<IEngineRpcModule>(inner), LimboLogs.Instance);
        _ = new ExternalClInterceptingEngineRpcModule(inner, detector);
        EngineDriver driver = new(detector, LimboLogs.Instance);

        await driver.ForkchoiceUpdated(head, safe, finalized);

        await inner.Received(1).engine_forkchoiceUpdatedV3(
            Arg.Is<ForkchoiceStateV1>(s => s.HeadBlockHash == head && s.SafeBlockHash == safe && s.FinalizedBlockHash == finalized),
            Arg.Is<PayloadAttributes?>(a => a == null));
        await inner.DidNotReceiveWithAnyArgs().engine_forkchoiceUpdatedV4(default!, default, default);
        await inner.DidNotReceiveWithAnyArgs().engine_forkchoiceUpdatedV5(default!, default, default);
    }
}
