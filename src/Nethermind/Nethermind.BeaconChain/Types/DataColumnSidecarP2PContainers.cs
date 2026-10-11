// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Fulu req/resp <c>DataColumnSidecarsByRangeRequest</c> (p2p-interface.md).</summary>
/// <remarks><c>columns</c> is the bare SSZ <c>List[ColumnIndex, NUMBER_OF_COLUMNS]</c> (128).</remarks>
[SszContainer]
public partial class DataColumnSidecarsByRangeRequest
{
    public ulong StartSlot { get; set; }
    public ulong Count { get; set; }
    [SszList(128)] // NUMBER_OF_COLUMNS
    public ulong[]? Columns { get; set; }
}

/// <summary>Fulu req/resp <c>DataColumnsByRootIdentifier</c> (p2p-interface.md).</summary>
[SszContainer]
public partial class DataColumnsByRootIdentifier
{
    public Hash256? BlockRoot { get; set; }
    [SszList(128)] // NUMBER_OF_COLUMNS
    public ulong[]? Columns { get; set; }
}

/// <summary>A <c>data_column_sidecars_by_range</c> or <c>by_root</c> dial: the wire request and whether the response must be Gloas-shaped.</summary>
/// <remarks>
/// Fulu and Gloas share request SSZ. Libp2p dispatches only the first <c>ISessionProtocol</c> closing,
/// so the response shape travels with the request rather than through separate generic closings.
/// </remarks>
/// <param name="OnSidecar">Receives each Fulu sidecar of a by-range response as it is read and structurally checked, so a reply that later fails still leaves what it delivered; unused for by-root and Gloas dials.</param>
public readonly record struct DataColumnSidecarsDial<TRequest>(TRequest Request, bool Gloas, Action<DataColumnSidecar>? OnSidecar = null);

/// <summary>A data column sidecars response in the shape its <see cref="DataColumnSidecarsDial{TRequest}"/> asked for; the other list is empty.</summary>
public sealed record ForkedDataColumnSidecars(IReadOnlyList<DataColumnSidecar> Fulu, IReadOnlyList<DataColumnSidecarGloas> Gloas);

/// <summary>Fulu req/resp <c>DataColumnsByRootIdentifiers</c>: bare SSZ list, <c>LIMIT = MAX_REQUEST_BLOCKS_DENEB</c> (p2p-interface.md).</summary>
[SszContainer(isCollectionItself: true)]
public partial class DataColumnSidecarsByRootRequest
{
    [SszList(128)] // MAX_REQUEST_BLOCKS_DENEB
    public DataColumnsByRootIdentifier[]? Identifiers { get; set; }
}
