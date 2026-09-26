// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Fulu req/resp <c>DataColumnSidecarsByRangeRequest</c> (p2p-interface.md).</summary>
/// <remarks>
/// The spec's <c>columns</c> field is typed <c>DataColumnIndices = List[ColumnIndex, NUMBER_OF_COLUMNS]</c>;
/// inlined here as a plain <c>[SszList]</c> field rather than a separate named type, matching how
/// <see cref="BeaconBlocksByRootRequest"/> already inlines its bare-list shape in this file.
/// </remarks>
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
/// The request SSZ is the same for both shapes (gloas/p2p-interface.md changes only the <c>DataColumnSidecar</c> response),
/// and the libp2p host dispatches a dial through the first <c>ISessionProtocol</c> closing of a protocol type only,
/// so one closing per protocol carries the shape with the request.
/// </remarks>
public readonly record struct DataColumnSidecarsDial<TRequest>(TRequest Request, bool Gloas);

/// <summary>A data column sidecars response in the shape its <see cref="DataColumnSidecarsDial{TRequest}"/> asked for; the other list is empty.</summary>
public sealed record ForkedDataColumnSidecars(IReadOnlyList<DataColumnSidecar> Fulu, IReadOnlyList<DataColumnSidecarGloas> Gloas);

/// <summary>Fulu req/resp <c>DataColumnsByRootIdentifiers</c>: bare SSZ list, <c>LIMIT = MAX_REQUEST_BLOCKS_DENEB</c> (p2p-interface.md).</summary>
[SszContainer(isCollectionItself: true)]
public partial class DataColumnSidecarsByRootRequest
{
    [SszList(128)] // MAX_REQUEST_BLOCKS_DENEB
    public DataColumnsByRootIdentifier[]? Identifiers { get; set; }
}
