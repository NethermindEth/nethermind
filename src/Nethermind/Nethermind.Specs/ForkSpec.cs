// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public readonly record struct ForkSpec(long? Block, ulong? Timestamp, IReleaseSpec Spec)
{
    public ForkSpec(ulong timestamp, IReleaseSpec spec) : this(null, timestamp, spec) { }
    public ForkSpec(long block, IReleaseSpec spec) : this(block, null, spec) { }
}
