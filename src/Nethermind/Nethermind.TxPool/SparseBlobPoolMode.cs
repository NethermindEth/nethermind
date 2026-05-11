// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool;

/// <summary>
/// Controls the local EIP-8070 sparse blob pool behaviour used when fetching blob cells from eth/72 peers.
/// </summary>
public enum SparseBlobPoolMode
{
    /// <summary>Use standard non-supernode behaviour: choose provider or sampler behaviour per transaction using the configured provider probability.</summary>
    Auto,

    /// <summary>Fetch every announced cell and use random peer selection to spread requests across providers and samplers.</summary>
    Supernode
}
