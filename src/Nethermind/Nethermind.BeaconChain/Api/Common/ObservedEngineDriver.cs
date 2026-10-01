// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Publishes execution-engine availability from completed driver calls.</summary>
internal sealed class EngineAvailability
{
    private int _offline;
    public bool IsOffline => Volatile.Read(ref _offline) != 0;
    public void SetOffline(bool offline) => Volatile.Write(ref _offline, offline ? 1 : 0);
}

internal sealed class ObservedEngineDriver(IEngineDriver inner, EngineAvailability availability) : IEngineDriver
{
    public SignedBeaconBlock? CurrentBlock { get => inner.CurrentBlock; set => inner.CurrentBlock = value; }
    public bool HasAnsweredNewPayload => inner.HasAnsweredNewPayload;

    public async Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
    {
        try
        {
            PayloadStatusV1 result = await inner.ForkchoiceUpdated(headExecHash, safeExecHash, finalizedExecHash);
            availability.SetOffline(false);
            return result;
        }
        catch (EngineUnavailableException)
        {
            availability.SetOffline(true);
            throw;
        }
    }

    public ExecutionStatus NotifyNewPayload(BeaconBlockBody body)
    {
        try
        {
            ExecutionStatus result = inner.NotifyNewPayload(body);
            availability.SetOffline(false);
            return result;
        }
        catch (EngineUnavailableException)
        {
            availability.SetOffline(true);
            throw;
        }
    }

    public ExecutionStatus NotifyNewPayload(ExecutionPayloadGloas payload, Hash256?[] versionedHashes, Hash256 parentBeaconBlockRoot, ExecutionRequestsGloas executionRequests)
    {
        try
        {
            ExecutionStatus result = inner.NotifyNewPayload(payload, versionedHashes, parentBeaconBlockRoot, executionRequests);
            availability.SetOffline(false);
            return result;
        }
        catch (EngineUnavailableException)
        {
            availability.SetOffline(true);
            throw;
        }
    }
}
