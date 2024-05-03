// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace EngineRequestsGenerator;

[AttributeUsage(AttributeTargets.Field)]
public class TestCaseMetadataAttribute(string title, string description) : Attribute
{
    public string Title { get; set; } = title;

    public string Description { get; set; } = description;
}
