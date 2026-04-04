// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class RunnerCommandAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; init; } = "";
    public bool IsDefault { get; init; }
}
