// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NLogLevel = NLog.LogLevel;

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

    public static void ValidateLogLevel(string optionName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{optionName} must not be empty.", optionName);
        }

        try
        {
            _ = NLogLevel.FromString(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{optionName} must be one of Trace, Debug, Info, Warn, or Error.", optionName, exception);
        }
    }
}
