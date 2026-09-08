// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public readonly record struct ForkSpec(ulong? Block, ulong? Timestamp, IReleaseSpec Spec)
{
    public static ForkSpec AtTimestamp(ulong timestamp, IReleaseSpec spec) => new(null, timestamp, spec);
    public static ForkSpec AtBlock(ulong block, IReleaseSpec spec) => new(block, null, spec);
}
