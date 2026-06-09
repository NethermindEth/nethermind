// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class GasCapExtensions
{
    public static bool IsGasCapped(this long? gasCap) => gasCap is not null and not 0;

    public static long EffectiveGasCap(this long? gasCap) => gasCap.IsGasCapped() ? gasCap!.Value : long.MaxValue;
}
