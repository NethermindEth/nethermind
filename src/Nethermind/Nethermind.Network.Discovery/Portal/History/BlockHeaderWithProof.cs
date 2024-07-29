// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.History;

public class PortalBlockHeaderWithProof
{
    public byte[] Header { get; set; } = Array.Empty<byte>();
    public byte[] Proof { get; set; } = Array.Empty<byte>();
}

public class PortalBlockBodyPostShanghai
{
    public byte[][] Transactions { get; set; } = Array.Empty<byte[]>();
    public byte[] Uncles { get; set; } = Array.Empty<byte>();
    public byte[] Withdrawals { get; set; } = Array.Empty<byte>();
}

public class PortalReceiptsSSZ
{
    public byte[][]? AsBytes { get; set; }
}
