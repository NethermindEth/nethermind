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
        BlockNumber = [forkActivation.BlockNumber],
        Timestamp = [forkActivation.Timestamp ?? 0ul]
    };

    public readonly ForkActivation ToForkActivation()
    {
        if (BlockNumber is not { Length: 1 })
            throw new InvalidDataException($"{nameof(BlockNumber)} must have exactly one element.");

        if (Timestamp is not { Length: 1 })
            throw new InvalidDataException($"{nameof(Timestamp)} must have exactly one element.");

        return new(checked(BlockNumber[0]), Timestamp[0]);
    }
}
