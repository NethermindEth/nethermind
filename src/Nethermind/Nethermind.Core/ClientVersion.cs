//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.Core
{
    public static class ClientVersion
    {
        private static string _gitTag;

        private static string _osName;
        private static string _date;

        static ClientVersion()
        {
            switch (RuntimeInformation.OSDescription.Split(" ")[0])
            {
                case "Microsoft":
                    // Replace "Microsoft Windows" => "Windows"
                    _osName = "Windows";
                    break;
                case "Darwin":
                    // Replace "Darwin" => "macOS"
                    _osName = "macOS";
                    break;
                default:
                    // Don't do anything as "Linux" is "Linux", "FreeBSD" is "FreeBSD"...
                    _osName = RuntimeInformation.OSDescription.Split(" ")[0];
                    break;
            }

            _date = DateTime.UtcNow.ToString("yyyyMMdd");
            _gitTag = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "git-hash")) ? File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "git-hash")).Trim().Replace("g", "") : string.Empty;

            Description = $"Nethermind/v{Version}/{RuntimeInformation.OSArchitecture}-{_osName}/{RuntimeInformation.FrameworkDescription.Trim().Replace(".NET ", "").Replace(" ", "")}";
        }

        public static string Version => $"{_gitTag}-{_date}";

        public static string Description { get; }
    }
}