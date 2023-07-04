// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Modules
{
    public class CliModuleAttribute : Attribute
    {
        public string ModuleName { get; }

        public CliModuleAttribute(string moduleName)
        {
            ModuleName = moduleName;
        }
    }
}
