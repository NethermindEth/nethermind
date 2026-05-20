// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config;

[AttributeUsage(AttributeTargets.Property)]
public class ConfigItemAttribute : Attribute
{
    public string Description { get; set; } = "";

    public string? DefaultValue { get; set; }

    public bool HiddenFromDocs { get; set; }

    public bool DisabledForCli { get; set; }

    public string EnvironmentVariable { get; set; } = "";

    public bool IsPortOption { get; set; }

    public string CliOptionAlias { get; set; } = "";

    /// <summary>
    /// Marks the property as containing secrets (passwords, API keys, private keys, ...).
    /// Such values must never be written to logs or other diagnostic surfaces.
    /// </summary>
    public bool IsSensitive { get; set; }
}
