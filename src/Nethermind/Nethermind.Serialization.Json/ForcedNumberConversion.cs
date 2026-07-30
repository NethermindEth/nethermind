// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json;

public static class ForcedNumberConversion
{
    [ThreadStatic]
    private static NumberConversion _threadCache;

    public static NumberConversion Value
    {
        get => _threadCache;
        set => _threadCache = value;
    }
}
