// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;

namespace Nethermind.Peering.Mothra
{
    public delegate void RpcReceivedEventHandler(ReadOnlySpan<byte> methodUtf8, int requestResponseFlag, ReadOnlySpan<byte> peerUtf8, ReadOnlySpan<byte> data);
}
