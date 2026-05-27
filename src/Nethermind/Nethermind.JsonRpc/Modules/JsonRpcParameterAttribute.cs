// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class JsonRpcParameterAttribute : Attribute
    {
        public string? Description { get; set; }

        public string? ExampleValue { get; set; }

        /// <summary>
        /// When <see langword="true"/>, the JSON-RPC binding layer treats this parameter as required even if the C# signature provides a default value.
        /// </summary>
        /// <remarks>
        /// Omitted, <see langword="null"/>, and empty-string inputs still fail with a missing-required-argument error for parameters marked this way.
        /// </remarks>
        public bool IsRequired { get; set; }
    }
}
