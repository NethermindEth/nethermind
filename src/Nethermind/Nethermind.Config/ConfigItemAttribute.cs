// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config
{
    public class ConfigItemAttribute : Attribute
    {
        public string Description { get; set; }

        public string DefaultValue { get; set; }

        public bool HiddenFromDocs { get; set; }

        public bool DisabledForCli { get; set; }

        public string EnvironmentVariable { get; set; }
    }
}
