// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Peering.Mothra
{
    public delegate void PeerDiscoveredEventHandler(ReadOnlySpan<byte> peerUtf8);
}
