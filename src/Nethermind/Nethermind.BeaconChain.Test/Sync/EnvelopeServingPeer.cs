// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.Sync;

internal sealed class EnvelopeServingPeer(
    string id,
    ulong headSlot,
    Func<ulong, ulong, IReadOnlyList<SignedExecutionPayloadEnvelope>>? byRange = null,
    Func<Hash256[], IReadOnlyList<SignedExecutionPayloadEnvelope>>? byRoot = null,
    Func<ulong, ulong, IReadOnlyList<ForkedSignedBeaconBlock>>? blocksByRange = null,
    Func<Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>? blocksByRoot = null,
    Func<DataColumnsByRootIdentifier[], IReadOnlyList<DataColumnSidecarGloas>>? gloasColumnsByRoot = null,
    ulong earliestAvailableSlot = 0) : IBeaconSyncPeer
{
    public List<(ulong StartSlot, ulong Count)> RangeRequests { get; } = [];
    public List<Hash256[]> RootRequests { get; } = [];
    public List<DataColumnsByRootIdentifier[]> ColumnRootRequests { get; } = [];
    public List<PeerFailureReason> Reports { get; } = [];

    public string Id => id;
    public ulong HeadSlot => headSlot;
    public ulong EarliestAvailableSlot => earliestAvailableSlot;

    public PeerColumnCustody Custody { get; } = new(Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(static c => (ulong)c), isAdvertised: true);

    public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<DataColumnSidecar>>([]);

    public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ulong startSlot, ulong count, CancellationToken token) =>
        Task.FromResult(blocksByRange?.Invoke(startSlot, count) ?? []);

    public Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(Hash256[] roots, CancellationToken token) =>
        Task.FromResult(blocksByRoot?.Invoke(roots) ?? []);

    public Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<DataColumnSidecar>>([]);

    public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ulong startSlot, ulong count, ulong[] columns, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<DataColumnSidecarGloas>>([]);

    public Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
    {
        ColumnRootRequests.Add(identifiers);
        return Task.FromResult(gloasColumnsByRoot?.Invoke(identifiers) ?? []);
    }

    public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ulong startSlot, ulong count, CancellationToken token)
    {
        RangeRequests.Add((startSlot, count));
        return Task.FromResult(byRange?.Invoke(startSlot, count) ?? []);
    }

    public Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(Hash256[] roots, CancellationToken token)
    {
        RootRequests.Add(roots);
        return Task.FromResult(byRoot?.Invoke(roots) ?? []);
    }

    public void ReportFailure(PeerFailureReason reason, string? detail = null) => Reports.Add(reason);
}
