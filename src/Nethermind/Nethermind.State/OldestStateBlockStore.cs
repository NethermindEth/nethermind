// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.State;

/// <summary>
/// Absolute lower bound of the persisted state window. Written by snap-sync finalization
/// and full pruning; read by RPC (<c>eth_capabilities</c>) and the startup staleness check.
/// </summary>
public sealed class OldestStateBlockStore(IDb metadataDb)
    : MetadataLongStore(metadataDb, MetadataDbKeys.OldestStateBlock);
