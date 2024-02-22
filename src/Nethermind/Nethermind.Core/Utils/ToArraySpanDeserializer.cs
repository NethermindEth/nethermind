// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Utils;

public readonly struct ToArraySpanDeserializer: ISpanDeserializer<byte[]?>
{
    public static ToArraySpanDeserializer Instance = new();

    public byte[]? Deserialize(ReadOnlySpan<byte> span)
    {
        if (span.IsNull()) return null;
        return span.ToArray();
    }
}
