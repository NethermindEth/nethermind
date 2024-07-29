// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery;

public interface IUTPTransfer
{
    Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token);
}
