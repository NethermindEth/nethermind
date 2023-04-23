// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

using System.Globalization;

namespace Nethermind.Init.Cpu;

internal static class DefaultCultureInfo
{
    public static readonly CultureInfo Instance;

    static DefaultCultureInfo()
    {
        Instance = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        Instance.NumberFormat.NumberDecimalSeparator = ".";
    }
}
