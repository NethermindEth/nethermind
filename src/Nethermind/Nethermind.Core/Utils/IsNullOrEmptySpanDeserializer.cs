// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Utils;

public readonly struct IsNullOrEmptySpanDeserializer: ISpanDeserializer<bool>
{
    public static IsNullOrEmptySpanDeserializer Instance = new();

    public bool Deserialize(ReadOnlySpan<byte> span)
    {
        return span.IsNullOrEmpty();
    }
}
