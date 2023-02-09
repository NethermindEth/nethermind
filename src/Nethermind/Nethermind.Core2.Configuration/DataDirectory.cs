// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
