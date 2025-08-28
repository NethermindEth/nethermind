// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

public class CompositePrecompileChecker : IPrecompileChecker
{
    private readonly IPrecompileChecker[] _checkers;

    public CompositePrecompileChecker(params IPrecompileChecker[] checkers)
    {
        if (checkers is null || checkers.Length == 0)
            throw new ArgumentException("At least one precompile checker must be provided", nameof(checkers));

        _checkers = checkers;
    }

    public bool IsPrecompile(Address address, IReleaseSpec spec)
    {
        foreach (var pc in _checkers)
        {
            if (pc.IsPrecompile(address, spec))
                return true;
        }

        return false;
    }
}
