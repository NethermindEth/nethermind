// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

public class CompositePrecompileChecker(params IPrecompileChecker[] checkers) : IPrecompileChecker
{
    public bool IsPrecompile(Address address, IReleaseSpec spec)
    {
        return checkers.Any(checker => checker.IsPrecompile(address, spec));
    }
}
