// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public interface INamedReleaseSpec : IReleaseSpec
{
    public static abstract IReleaseSpec Instance { get; }
    bool Released { get; }
}
