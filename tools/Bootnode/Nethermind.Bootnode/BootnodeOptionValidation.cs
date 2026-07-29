// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Bootnode;

internal static class BootnodeOptionValidation
{
    public static void ValidatePort(string optionName, int value)
    {
        if ((uint)(value - 1) >= ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be between 1 and 65535.");
        }
    }

    public static void ValidatePositive(string optionName, int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be greater than 0.");
        }
    }

    public static void ValidateNonNegative(string optionName, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(optionName, value, $"{optionName} must be greater than or equal to 0.");
        }
    }
}
