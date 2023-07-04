// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Drawing;

namespace Nethermind.Cli.Console
{
    public class BasicColorScheme : ColorScheme
    {
        private BasicColorScheme() { }

        public static ColorScheme Instance { get; } = new BasicColorScheme();
        public override Color BackgroundColor => Color.Black;
        public override Color ErrorColor => Color.Red;
        public override Color Text => Color.White;
        public override Color Comment => Color.LightBlue;
        public override Color Keyword => Color.Magenta;
        public override Color String => Color.Coral;
        public override Color Good => Color.GreenYellow;
        public override Color LessImportant => Color.LightGray;
        public override Color Interesting => Color.Aqua;
    }
}
