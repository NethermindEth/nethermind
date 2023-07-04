// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    [AttributeUsage(AttributeTargets.Method)]
    public class JsonRpcMethodAttribute : Attribute
    {
        public string Description { get; set; }

        public string? EdgeCaseHint { get; set; }

        public bool IsImplemented { get; set; } = true;

        public bool IsSharable { get; set; } = true;

        public RpcEndpoint Availability { get; set; } = RpcEndpoint.All;

        public string? ResponseDescription { get; set; }

        public string? ExampleResponse { get; set; }
    }
}
