// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public static class ReleaseSpecExtensions
{
    public static IReleaseSpec ForSystemTransaction(this IReleaseSpec spec, bool isAura, bool isGenesis) =>
        spec switch
        {
            ReleaseSpec releaseSpec => releaseSpec.SystemSpec,
            { IsEip158Enabled: false } => spec,
            _ when !isAura && isGenesis => spec,
            _ => throw new InvalidOperationException($"Unexpected IReleaseSpec implementation: {spec.GetType().Name}")
        };
}
