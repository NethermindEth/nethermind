// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Deneb <c>NewPayloadRequest</c> (specs/deneb/beacon-chain.md): the argument of <c>verify_and_notify_new_payload</c>.</summary>
[SszContainer]
public partial class NewPayloadRequestDeneb
{
    public ExecutionPayload? ExecutionPayload { get; set; }
    /// <remarks><c>VersionedHashes</c>: <c>List[VersionedHash, MAX_BLOB_COMMITMENTS_PER_BLOCK]</c>.</remarks>
    [SszList(4096)]
    public Hash256[]? VersionedHashes { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
}

/// <summary>Electra <c>NewPayloadRequest</c> (specs/electra/beacon-chain.md, unchanged in Fulu): Deneb's plus <c>execution_requests</c>.</summary>
[SszContainer]
public partial class NewPayloadRequest
{
    public ExecutionPayload? ExecutionPayload { get; set; }
    /// <remarks><c>VersionedHashes</c>: <c>List[VersionedHash, MAX_BLOB_COMMITMENTS_PER_BLOCK]</c>.</remarks>
    [SszList(4096)]
    public Hash256[]? VersionedHashes { get; set; }
    public Hash256? ParentBeaconBlockRoot { get; set; }
    public ExecutionRequests? ExecutionRequests { get; set; }
}

/// <summary>
/// Gloas <c>NewPayloadRequest</c> (specs/gloas/beacon-chain.md, "Modified containers"): <c>ProgressiveContainer</c>,
/// <c>ACTIVE_FIELDS</c> width 4, over the Gloas payload and execution requests.
/// </summary>
[SszContainer]
public partial class NewPayloadRequestGloas
{
    [SszField(0)]
    public ExecutionPayloadGloas? ExecutionPayload { get; set; }
    /// <remarks><c>VersionedHashes</c>: <c>List[VersionedHash, MAX_BLOB_COMMITMENTS_PER_BLOCK]</c>.</remarks>
    [SszField(1)]
    [SszList(4096)]
    public Hash256[]? VersionedHashes { get; set; }
    [SszField(2)]
    public Hash256? ParentBeaconBlockRoot { get; set; }
    [SszField(3)]
    public ExecutionRequestsGloas? ExecutionRequests { get; set; }
}
