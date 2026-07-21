// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ChainConfig
{
    public ulong ChainId { get; set; }

    public ForkConfig ActiveFork { get; set; }
}
