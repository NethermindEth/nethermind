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
// 

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
