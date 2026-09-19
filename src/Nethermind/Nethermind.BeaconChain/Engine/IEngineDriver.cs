// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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

    /// <inheritdoc cref="EngineDriver.LastNewPayloadStatus"/>
    PayloadStatusV1? LastNewPayloadStatus { get; }

    /// <inheritdoc cref="EngineDriver.ForkchoiceUpdated"/>
    Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash);
}
