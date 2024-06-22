// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging;
public class Progress
{
    private static char[] _progressChars = { ' ', '⡀', '⡄', '⡆', '⡇', '⣇', '⣧', '⣷', '⣿' };

    public static string GetMeter(float value, int max, int width = 40)
    {
        width = Math.Max(4, width - 3);
        float progressF = value / max * width;
        int progress = (int)Math.Floor(progressF);
        int progressChar = (int)((progressF - progress) * _progressChars.Length);

        return $"[{new string('⣿', progress)}{_progressChars[progressChar]}{new string(' ', width - progress - 1)}]";
    }
}
