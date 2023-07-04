// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public class JsonRpcParameterAttribute : Attribute
    {
        public string? Description { get; set; }

        public string? ExampleValue { get; set; }
    }
}
