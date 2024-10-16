// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class EncodingExtensions
{
    // TODO add unit tests
    public static bool TryGetString(this Encoding encoding, ReadOnlySequence<byte> bytes, [NotNullWhen(true)] out string? result)
    {
        try
        {
            result = encoding.GetString(bytes);
            return true;
        }
        catch (Exception)
        {
            result = null;
            return false;
        }
    }
}
