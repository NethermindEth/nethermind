// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Modules
{
    public class CliPropertyAttribute : Attribute
    {
        public string ObjectName { get; }
        public string PropertyName { get; }
        public string? Description { get; set; }

        public string? ResponseDescription { get; set; }

        public string? ExampleResponse { get; set; }

        public CliPropertyAttribute(string objectName, string propertyName)
        {
            ObjectName = objectName;
            PropertyName = propertyName;
        }

        public override string ToString()
        {
            return $"{ObjectName}.{PropertyName}{(Description is null ? "" : $" {Description}")}";
        }
    }
}
