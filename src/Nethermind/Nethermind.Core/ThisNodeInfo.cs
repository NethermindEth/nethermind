// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace Nethermind.Core
{
    public static class ThisNodeInfo
    {
        private static readonly ConcurrentDictionary<string, string> _nodeInfoItems = new();

        public static void AddInfo(string infoDescription, string value)
        {
            _nodeInfoItems.TryAdd(infoDescription, value);
        }

        public static string BuildNodeInfoScreen()
        {
            StringBuilder builder = new();
            builder.AppendLine(NethermindLogo);
            builder.AppendLine("-----------------------------  Initialization Completed  -----------------------------");
            builder.AppendLine();

            foreach ((string key, string value) in _nodeInfoItems.OrderByDescending(static ni => ni.Key))
            {
                builder.AppendLine($"{key} {value}");
            }

            builder.Append("--------------------------------------------------------------------------------------");
            return builder.ToString();
        }

        private static string NethermindLogo = "\n\n" +
       "\u001b[36m        ------             \u001b[38;5;208m   ~~~~~~~~        \u001b[37m\n" +
       "\u001b[36m     --------- ----        \u001b[38;5;208m~~~~~~~~~~~~~~     \u001b[37m\n" +
       "\u001b[36m   -  -------  ------      \u001b[38;5;208m  ~~~~~~~~~~~~~~   \u001b[37m\n" +
       "\u001b[36m -----  -      ----        \u001b[38;5;208m   ~~~    ~~~~~~~~ \u001b[37m       ++   ++   +++++  ++++++  ++   ++   +++++  ++++++    ++    ++    ++   ++   ++   +++++ \n" +
       "\u001b[36m ------         -          \u001b[38;5;208m            ~~~    \u001b[37m       +++  ++  ++        ++    ++   ++  ++      ++   ++  ++++   +++   ++   +++  ++   ++  ++ \n" +
       "\u001b[36m-------                                   ----\u001b[37m       ++ + ++  ++++++    ++    +++++++  ++++++  ++++++   ++ +  + ++   ++   ++ + ++   ++  ++\n" +
       "\u001b[36m----                                   -------\u001b[37m       ++  +++  ++        ++    ++   ++  ++      ++  ++   ++ ++++ ++   ++   ++  +++   ++  ++\n" +
 "\u001b[38;5;208m    ~~~                  \u001b[36m    -         ------ \u001b[37m       ++   ++   +++++    ++    ++   ++   +++++  ++   ++  ++  ++  ++   ++   ++   ++   +++++ \n" +
 "\u001b[38;5;208m ~~~~~~~~      ~         \u001b[36m  ----      -  ----- \u001b[37m\n" +
 "\u001b[38;5;208m   ~~~~~~~~~~~~~~        \u001b[36m------  -------  -   \u001b[37m\n" +
 "\u001b[38;5;208m     ~~~~~~~~~~~~~~      \u001b[36m  ---- ---------     \u001b[37m\n" +
 "\u001b[38;5;208m        ~~~~~~~~         \u001b[36m       ------        \u001b[37m                                                                  https://www.nethermind.io\n";
    }
}
