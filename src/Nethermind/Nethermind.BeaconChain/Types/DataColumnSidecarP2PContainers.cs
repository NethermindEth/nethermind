// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

/// <summary>Fulu req/resp <c>DataColumnsByRootIdentifiers</c>: bare SSZ list, <c>LIMIT = MAX_REQUEST_BLOCKS_DENEB</c> (p2p-interface.md).</summary>
[SszContainer(isCollectionItself: true)]
public partial class DataColumnSidecarsByRootRequest
{
    [SszList(128)] // MAX_REQUEST_BLOCKS_DENEB
    public DataColumnsByRootIdentifier[]? Identifiers { get; set; }
}
