// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct SszForkActivation
{
    [SszList(1)]
    public ulong[] BlockNumber { get; set; }

    [SszList(1)]
    public ulong[] Timestamp { get; set; }

    public static SszForkActivation From(ForkActivation forkActivation) => new()
    {
        BlockNumber = [(ulong)forkActivation.BlockNumber],
        Timestamp = [forkActivation.Timestamp ?? 0ul]
    };

    public readonly ForkActivation ToForkActivation() =>
        new((long)BlockNumber.FirstOrDefault(), Timestamp.FirstOrDefault());
}
