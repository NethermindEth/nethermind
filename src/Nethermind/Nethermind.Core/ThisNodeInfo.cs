// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace Nethermind.Core
{
    public static class ThisNodeInfo
    {
        // Layout breakpoints based on what content can fit (without wrapping):
        //   - GlyphLogoMinWidth: room for the glyph art (47 cols + breathing room)
        //   - FullLogoMinWidth:  room for the figlet wordmark (69 cols + breathing room)
        // Below GlyphLogoMinWidth we fall back to a one-line text logo.
        private const int GlyphLogoMinWidth = 50;
        private const int FullLogoMinWidth = 75;

        // Cap on the separator/divider line width so it stays readable in very
        // wide terminals; the line is centered within the terminal.
        private const int SeparatorMaxWidth = 86;
        private const string SeparatorTitle = "  Initialization Completed  ";

        // Visible widths used to compute centered padding at runtime.
        private const int GlyphWidth = 47;
        private const int WordmarkWidth = 69;
        private const string Url = "https://www.nethermind.io";

        private const string Cyan = "\u001b[36m";
        private const string Orange = "\u001b[38;5;208m";
        private const string Reset = "\u001b[0m";
        private const string Dim = "\u001b[2m";

        private static readonly string[] _glyphLines =
        {
            Cyan + "        ------                " + Orange + "~~~~~~~~        " + Reset,
            Cyan + "     --------- ----        " + Orange + "~~~~~~~~~~~~~~     " + Reset,
            Cyan + "   -  -------  ------        " + Orange + "~~~~~~~~~~~~~~   " + Reset,
            Cyan + " -----  -      ----           " + Orange + "~~~    ~~~~~~~~ " + Reset,
            Cyan + " ------         -                      " + Orange + "~~~        " + Reset,
            Cyan + "-------                                   ----" + Reset,
            Cyan + "----                                   -------" + Reset,
            Orange + "    ~~~                  " + Cyan + "    -         ------ " + Reset,
            Orange + " ~~~~~~~~      ~         " + Cyan + "  ----      -  ----- " + Reset,
            Orange + "   ~~~~~~~~~~~~~~        " + Cyan + "------  -------  -   " + Reset,
            Orange + "     ~~~~~~~~~~~~~~      " + Cyan + "  ---- ---------     " + Reset,
            Orange + "        ~~~~~~~~         " + Cyan + "       ------        " + Reset,
        };

        // Dimmed (\u001b[2m) instead of colored white so the wordmark stays
        // readable on both light and dark terminal themes.
        private static readonly string[] _wordmarkLines =
        {
            Dim + " _   _ ______ _______ _    _ ______ _____  __  __ _____ _   _ _____  " + Reset,
            Dim + "| \\ | |  ____|__   __| |  | |  ____|  __ \\|  \\/  |_   _| \\ | |  __ \\ " + Reset,
            Dim + "|  \\| | |__     | |  | |__| | |__  | |__) | \\  / | | | |  \\| | |  | |" + Reset,
            Dim + "| . ` |  __|    | |  |  __  |  __| |  _  /| |\\/| | | | | . ` | |  | |" + Reset,
            Dim + "| |\\  | |____   | |  | |  | | |____| | \\ \\| |  | |_| |_| |\\  | |__| |" + Reset,
            Dim + "|_| \\_|______|  |_|  |_|  |_|______|_|  \\_\\_|  |_|_____|_| \\_|_____/ " + Reset,
        };

        private static readonly ConcurrentDictionary<string, string> _nodeInfoItems = new();

        public static void AddInfo(string infoDescription, string value) => _nodeInfoItems.TryAdd(infoDescription, value);

        public static string BuildNodeInfoScreen()
        {
            int w = GetTerminalWidth();
            StringBuilder builder = new();
            builder.AppendLine(BuildLogo(w));
            builder.AppendLine(BuildTitledDivider(w));
            builder.AppendLine();

            foreach ((string key, string value) in _nodeInfoItems.OrderByDescending(static ni => ni.Key))
            {
                builder.AppendLine($"{key} {value}");
            }

            builder.Append(BuildDivider(w));
            return builder.ToString();
        }

        private static string BuildLogo(int width)
        {
            if (width < GlyphLogoMinWidth)
            {
                return "\n" + Cyan + "Nethermind" + Reset + "  -  " + Url + "\n";
            }

            StringBuilder sb = new();
            sb.Append('\n').Append('\n');

            string glyphPad = LeadingPad(width, GlyphWidth);
            foreach (string line in _glyphLines)
            {
                sb.Append(glyphPad).Append(line).Append('\n');
            }

            if (width >= FullLogoMinWidth)
            {
                sb.Append('\n').Append('\n').Append('\n');
                string wordmarkPad = LeadingPad(width, WordmarkWidth);
                foreach (string line in _wordmarkLines)
                {
                    sb.Append(wordmarkPad).Append(line).Append('\n');
                }
            }

            sb.Append('\n').Append('\n');
            sb.Append(LeadingPad(width, Url.Length)).Append(Url).Append('\n');
            return sb.ToString();
        }

        private static string BuildTitledDivider(int width)
        {
            int barWidth = Math.Min(width, SeparatorMaxWidth);
            if (barWidth < SeparatorTitle.Length)
            {
                return SeparatorTitle.Trim();
            }
            int dashes = barWidth - SeparatorTitle.Length;
            int left = dashes / 2;
            int right = dashes - left;
            return LeadingPad(width, barWidth) + new string('-', left) + SeparatorTitle + new string('-', right);
        }

        private static string BuildDivider(int width)
        {
            int barWidth = Math.Min(width, SeparatorMaxWidth);
            return LeadingPad(width, barWidth) + new string('-', barWidth);
        }

        // Layout is anchored to a left-aligned "block" capped at SeparatorMaxWidth.
        // Content (logo, wordmark, dividers) centers within the block, but the
        // block itself sits flush against the left margin even in wide terminals,
        // so the whole screen reads as one column rather than floating mid-screen.
        private static string LeadingPad(int width, int contentWidth)
        {
            int blockWidth = Math.Min(width, SeparatorMaxWidth);
            int innerPad = (blockWidth - contentWidth) / 2;
            return innerPad <= 0 ? string.Empty : new string(' ', innerPad);
        }

        // Console.WindowWidth throws when stdout is redirected or no TTY is attached
        // (e.g. `docker run` without `-t`). Fall back to the canonical separator
        // width so the output stays reasonably aligned in log files.
        private static int GetTerminalWidth()
        {
            try
            {
                int w = Console.WindowWidth;
                return w > 0 ? w : SeparatorMaxWidth;
            }
            catch
            {
                return SeparatorMaxWidth;
            }
        }
    }
}
