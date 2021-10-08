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
using Microsoft.Extensions.Configuration;

namespace Nethermind.Core2.Configuration
{
    public class DataDirectory
    {
        private static readonly Environment.SpecialFolder[] _specialFolders = new[]
        {
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData
        };

        public const string Key = "DataDirectory";

        public DataDirectory(string setting)
        {
            Setting = setting ?? string.Empty;
            ResolvedPath = Resolve(Setting);
        }

        public string Setting { get; }
        
        public string ResolvedPath { get; }

        private string Resolve(string setting)
        {
            foreach (Environment.SpecialFolder specialFolder in _specialFolders)
            {
                string specialFolderToken = "{" + specialFolder + "}";
                if (setting.StartsWith(specialFolderToken))
                {
                    string specialFolderPath =
                        Environment.GetFolderPath(specialFolder, Environment.SpecialFolderOption.Create);
                    string resolvedSpecialPath = setting.Replace(specialFolderToken, specialFolderPath);
                    return resolvedSpecialPath;
                }
            }

            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string resolvedPath = Path.Combine(basePath, setting);
            return resolvedPath;
        }
    }
}