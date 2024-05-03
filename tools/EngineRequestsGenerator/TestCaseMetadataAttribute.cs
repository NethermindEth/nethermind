// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

[AttributeUsage(AttributeTargets.Field)]
public class TestCaseMetadataAttribute(string name, string description) : Attribute
{
    public string Name { get; set; } = name;

    public string Description { get; set; } = description;
}
