// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Cli.Modules
{
    public class CliFunctionAttribute : Attribute
    {
        public string ObjectName { get; }

        public string FunctionName { get; }

        public string? Description { get; set; }

        public string? ResponseDescription { get; set; }

        public string? ExampleResponse { get; set; }

        public CliFunctionAttribute(string objectName, string functionName)
        {
            ObjectName = objectName;
            FunctionName = functionName;
        }

        public override string ToString()
        {
            return $"{ObjectName}.{FunctionName}{(Description is null ? "" : $" {Description}")}";
        }
    }
}
