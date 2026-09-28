// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
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

/// <summary>
/// Regression coverage for gap 119: <see cref="StateTransition.INewPayloadNotifier.RequireEnvelopeSupport"/>
/// existed but nothing in production called it, so a driver still relying on the interface's
/// throwing default for the Gloas envelope overload would only be discovered at the node's first
/// live envelope rather than at startup.
/// </summary>
public class StartBeaconChainTests
{
    [Test]
    public void Execute_throws_when_the_engine_driver_does_not_implement_the_gloas_envelope_overload()
    {
        // The null service is never reached: the guard must throw before `service.Start()` is called.
        StartBeaconChain step = new(null!, new EnvelopeUnsupportedEngineDriver(), LimboLogs.Instance);

        Assert.That(() => step.Execute(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Gloas execution payload envelopes"));
    }

    [Test]
    public void Execute_does_not_throw_for_an_engine_driver_that_implements_the_gloas_envelope_overload()
    {
        ExternalClDetector detector = new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(() => Substitute.For<IEngineRpcModule>()), LimboLogs.Instance);
        EngineDriver engine = new(detector, LimboLogs.Instance);
        // `Start()` checks the empty store synchronously; its background run then NREs on the null
        // checkpoint sync, which its own top-level catch swallows and logs.
        BeaconChainService service = new(new BeaconChainConfig(), null!, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), null!, null!, null!, detector, LimboLogs.Instance);
        StartBeaconChain step = new(service, engine, LimboLogs.Instance);

        Assert.That(() => step.Execute(CancellationToken.None), Throws.Nothing);
    }

    /// <summary>Only implements the base overload, so <see cref="INewPayloadNotifier"/>'s throwing default answers the Gloas envelope call.</summary>
    private sealed class EnvelopeUnsupportedEngineDriver : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }
        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            throw new NotSupportedException();

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }
}
