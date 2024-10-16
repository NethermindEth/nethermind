// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;

namespace Nethermind.Core.Extensions;

public static class EncodingExtensions
{
    // TODO add unit tests
    public static bool TryGetString(this Encoding encoding, ReadOnlySpan<byte> bytes, int charCount, out string? result, out bool fullyRead)
    {
        var buffer = new char[charCount];
        try
        {
            var readCount = encoding.GetChars(bytes, buffer);
            result = new(buffer.AsSpan(0, readCount));
            fullyRead = true;
            return true;
        }
        catch (ArgumentException exception) when (exception.ParamName == "chars")
        {
            // Chars array overflow, buffer should be fully populated
            result = new(buffer);
            fullyRead = false;
            return true;
        }
        catch (Exception)
        {
            result = null;
            fullyRead = false;
            return false;
        }
    }
}
