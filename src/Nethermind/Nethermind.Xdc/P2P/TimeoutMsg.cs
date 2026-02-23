// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P.Messages;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.P2P;

internal class TimeoutMsg : P2PMessage
{
    public override int PacketType => Xdpos2MessageCode.TimeoutMsg;
    public override string Protocol => "eth";
    public Timeout Timeout { get; set; }
    public override string ToString() => $"{nameof(TimeoutMsg)}({Timeout})";
}
