// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Drawing;
using System.Globalization;

namespace Nethermind.Cli.Console
{
    public abstract class ColorScheme
    {
        public abstract Color BackgroundColor { get; }
        public abstract Color ErrorColor { get; }
        public abstract Color Text { get; }
        public abstract Color Comment { get; }
        public abstract Color Keyword { get; }
        public abstract Color String { get; }
        public abstract Color Good { get; }
        public abstract Color LessImportant { get; }
        public abstract Color Interesting { get; }

        protected static Color FromHex(string hex)
        {
            return Color.FromArgb(int.Parse(hex.Replace("#", ""), NumberStyles.AllowHexSpecifier));
        }
    }
}
