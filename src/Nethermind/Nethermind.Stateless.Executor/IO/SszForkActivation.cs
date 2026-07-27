// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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

    public static SszForkActivation From(ForkActivation forkActivation)
    {
        if (forkActivation.Timestamp is { } timestamp)
        {
            return new()
            {
                BlockNumber = [],
                Timestamp = [timestamp]
            };
        }

        return new()
        {
            BlockNumber = [forkActivation.BlockNumber],
            Timestamp = []
        };
    }

    public readonly ForkActivation ToForkActivation()
    {
        Validate();

        return (BlockNumber.Length, Timestamp.Length) switch
        {
            (0, 1) => ForkActivation.TimestampOnly(Timestamp[0]),
            (1, 0) => new(BlockNumber[0]),
            _ => new(BlockNumber[0], Timestamp[0])
        };
    }

    /// <summary>
    /// Returns whether every configured activation bound is active for the supplied block.
    /// </summary>
    internal readonly bool IsActive(BlockHeader header)
    {
        Validate();

        return (BlockNumber.Length == 0 || header.Number >= BlockNumber[0])
            && (Timestamp.Length == 0 || header.Timestamp >= Timestamp[0]);
    }

    private readonly void Validate()
    {
        if (BlockNumber is not { Length: <= 1 })
            throw new InvalidDataException($"{nameof(BlockNumber)} must have at most one element.");

        if (Timestamp is not { Length: <= 1 })
            throw new InvalidDataException($"{nameof(Timestamp)} must have at most one element.");

        if (BlockNumber.Length == 0 && Timestamp.Length == 0)
            throw new InvalidDataException($"{nameof(BlockNumber)} or {nameof(Timestamp)} must have one element.");
    }
}
