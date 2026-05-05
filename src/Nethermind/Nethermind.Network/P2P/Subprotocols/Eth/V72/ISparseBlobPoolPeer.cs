// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

public interface ISparseBlobPoolPeer
{
    PublicKey Id { get; }
    bool IsClosing { get; }
    bool TrySendGetCells(Hash256 hash, BlobCellMask requestMask);
    void DisconnectSparseBlobPeer(DisconnectReason reason, string details);
}
