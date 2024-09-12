// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Logging;
public static class Progress
{
    private const int Width = 40;
    private const int WidthBar = Width - 3;

    private static readonly string[] _progressChars = [" ", "⡀", "⡄", "⡆", "⡇", "⣇", "⣧", "⣷", "⣿"];
    private static readonly string[] _fullChunks = CreateChunks('⣿', "[", "");
    private static readonly string[] _emptyChunks = CreateChunks(' ', "", "]");

    private static string[] CreateChunks(char ch, string start, string end)
    {
        var chunks = new string[WidthBar + 1];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = start + new string(ch, i) + end;
        }

        return chunks;
    }

    public static string GetMeter(float value, int max)
    {
        float progressF = Math.Clamp(value / max, 0, 1) * WidthBar;
        int progress = (int)Math.Floor(progressF);
        int progressChar = Math.Clamp((int)((progressF - progress) * _progressChars.Length), 0, _progressChars.Length - 1);
        int empty = Math.Clamp(WidthBar - progress - 1, 0, WidthBar);

        return string.Concat(_fullChunks[progress], _progressChars[progressChar], _emptyChunks[empty]);
    }
}
