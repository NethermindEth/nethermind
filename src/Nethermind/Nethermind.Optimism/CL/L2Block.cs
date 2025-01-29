// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Optimism.CL;

public class L2Block
{
    public required ulong Number { get; set; }
    public required Hash256 Hash { get; set; }
    public required ulong Timestamp { get; set; }
    public required Hash256 ParentHash { get; set; }
    public required SystemConfig SystemConfig { get; set; }
}
