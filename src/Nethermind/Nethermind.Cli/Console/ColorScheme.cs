//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
