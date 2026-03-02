// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.P2P;

internal class SyncInfoMsg : P2PMessage
{
    public override int PacketType => XdcMessageCode.SyncInfoMsg;
    public override string Protocol => "eth";
    public SyncInfo SyncInfo { get; set; }
    public override string ToString() => $"{nameof(SyncInfo)}({SyncInfo})";
}
