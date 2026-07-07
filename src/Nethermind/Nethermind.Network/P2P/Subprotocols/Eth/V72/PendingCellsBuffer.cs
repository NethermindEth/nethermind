// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// A batch of flattened blob cells received from a peer, buffered until they can be
/// validated against and merged into the pooled transaction.
/// </summary>
internal readonly record struct PendingCellsBuffer(BlobCellMask CellMask, byte[][] Cells, PublicKey SourcePeerId);
