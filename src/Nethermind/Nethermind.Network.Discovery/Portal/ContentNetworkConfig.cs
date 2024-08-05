// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;

namespace Nethermind.Network.Discovery.Portal;

public class ContentNetworkConfig
{
    public byte[] ProtocolId { get; set; } = Array.Empty<byte>();
    public int K { get; set; } = 16;
    public int A { get; set; } = 3;
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);
    public IEnr[] BootNodes { get; set; } = Array.Empty<IEnr>();
};
