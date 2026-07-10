// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Optimism;

public interface IOptimismReleaseSpec : IReleaseSpec
{
    bool IsOpGraniteEnabled { get; }
    bool IsOpHoloceneEnabled { get; }
    bool IsOpIsthmusEnabled { get; }
    bool IsOpJovianEnabled { get; }
    bool IsOpKarstEnabled { get; }
}
