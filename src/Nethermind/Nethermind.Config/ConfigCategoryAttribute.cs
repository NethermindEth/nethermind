// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config
{
    public class ConfigCategoryAttribute : Attribute
    {
        public string Description { get; set; }

        public bool HiddenFromDocs { get; set; }

        public bool DisabledForCli { get; set; }
    }
}
