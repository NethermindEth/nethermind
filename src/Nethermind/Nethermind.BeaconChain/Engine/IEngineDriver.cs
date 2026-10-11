// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// The engine API surface the sync orchestrator and block importer drive the execution layer
/// through; implemented by <see cref="EngineDriver"/> and scripted in tests.
/// </summary>
public interface IEngineDriver : INewPayloadNotifier
{
    /// <inheritdoc cref="EngineDriver.CurrentBlock"/>
    SignedBeaconBlock? CurrentBlock { get; set; }

    /// <inheritdoc cref="EngineDriver.HasAnsweredNewPayload"/>
    bool HasAnsweredNewPayload { get; }

    /// <inheritdoc cref="EngineDriver.IsAvailable"/>
    /// <remarks>Drivers that do not track call outcomes report no observed failure.</remarks>
    bool IsAvailable => true;

    /// <inheritdoc cref="EngineDriver.ForkchoiceUpdated"/>
    Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash);

    /// <summary>The block-body <see cref="INewPayloadNotifier.NotifyNewPayload(BeaconBlockBody)"/> that also returns the engine's <c>latestValidHash</c>.</summary>
    /// <remarks>The default reports no hash, which specs/bellatrix/optimistic-sync.md treats as naming only the payload in question.</remarks>
    ExecutionStatus NotifyNewPayload(BeaconBlockBody body, out Hash256? latestValidHash)
    {
        latestValidHash = null;
        return NotifyNewPayload(body);
    }
}
