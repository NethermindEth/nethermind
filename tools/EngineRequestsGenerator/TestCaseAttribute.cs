// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

[AttributeUsage(AttributeTargets.Field)]
public class TestCaseAttribute : Attribute
{
    public string Name { get; set; }

    public string Description { get; set; }

    public TestCaseAttribute(string Name, string description)
    {
        Description = description;
    }
}
