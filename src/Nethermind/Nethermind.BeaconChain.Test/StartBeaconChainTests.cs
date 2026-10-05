// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class StartBeaconChainTests
{
    [Test]
    public void Execute_throws_when_the_engine_driver_does_not_implement_the_gloas_envelope_overload()
    {
        StartBeaconChain step = new(null!, new EnvelopeUnsupportedEngineDriver(), LimboLogs.Instance);

        Assert.That(() => step.Execute(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Gloas execution payload envelopes"));
    }

    [Test]
    public void Execute_does_not_throw_for_an_engine_driver_that_implements_the_gloas_envelope_overload()
    {
        ExternalClDetector detector = new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(() => Substitute.For<IEngineRpcModule>()), LimboLogs.Instance);
        EngineDriver engine = Engine.TestEngineDriver.Create(detector);
        // `Start()` checks the empty store synchronously; its background run then NREs on the null
        // checkpoint sync, which its own top-level catch swallows and logs.
        BeaconChainService service = new(new BeaconChainConfig(), null!, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), null!, null!, null!, detector, LimboLogs.Instance);
        StartBeaconChain step = new(service, engine, LimboLogs.Instance);

        Assert.That(() => step.Execute(CancellationToken.None), Throws.Nothing);
    }

    /// <summary>Implements only the base overload so the interface's throwing Gloas default remains active.</summary>
    private sealed class EnvelopeUnsupportedEngineDriver : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            throw new NotSupportedException();

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
