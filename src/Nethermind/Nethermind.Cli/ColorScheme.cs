/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Drawing;
using System.Globalization;

namespace Nethermind.Cli
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

        public Color FromHex(string hex)
        {
            return Color.FromArgb(int.Parse(hex.Replace("#", ""), NumberStyles.AllowHexSpecifier));
        }
    }

    public class BasicColorScheme : ColorScheme
    {
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

    public class DraculaColorScheme : ColorScheme
    {
        public override Color BackgroundColor => FromHex("#282a36");
        public override Color ErrorColor => FromHex("#ff5555");
        public override Color Text => FromHex("#f8f8f2");
        public override Color Comment => FromHex("#6272a4");
        public override Color Keyword => FromHex("#ff79c6");
        public override Color String => FromHex("#f1fa8c");
        public override Color Good => FromHex("#50fa7b");
        public override Color LessImportant => FromHex("#999999");
        public override Color Interesting => FromHex("#8be9fd");

//        @very-dark-gray: #282a36; // Background
//        @dark-gray: #44475a; // Current Line & Selection
//        @gray: #666666;
//        @light-gray: #999999;
//        @very-light-gray: #f8f8f2; // Foreground
//        @blue: #6272a4; // Comment
//
//        @cyan: #8be9fd;
//        @soft-cyan: #66d9ef;
//        @green: #50fa7b;
//        @orange: #ffb86c;
//        @pink: #ff79c6;
//        @purple: #bd93f9;
//        @red: #ff5555;
//        @yellow: #f1fa8c;
//        @dark-yellow: #E6DB74;
//        @sandstone: #cfcfc2;
//
//        @lace: #f8f8f0;
//        @white: #ffffff;
//        @black: #000000;
    }
}