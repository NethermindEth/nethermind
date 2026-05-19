// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

public class NewPooledTransactionHashesMessage72(byte[] types, int[] sizes, Hash256[] hashes, byte[] cellMask) : P2PMessage
{
    public const int MaxCount = 2048;

    public override int PacketType => Eth72MessageCode.NewPooledTransactionHashes;
    public override string Protocol => "eth";

    public byte[] Types { get; } = types;
    public int[] Sizes { get; } = sizes;
    public Hash256[] Hashes { get; } = hashes;
    public byte[] CellMask { get; } = cellMask;

    public override string ToString() => $"{nameof(NewPooledTransactionHashesMessage72)}({Hashes.Length})";
}
