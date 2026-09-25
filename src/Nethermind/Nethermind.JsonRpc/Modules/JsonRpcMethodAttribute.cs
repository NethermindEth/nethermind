// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        /// <summary>Admits the method through the JSON-RPC EVM-execution gate (<c>JsonRpc.EvmExecutionMaxQueueWaitMs</c>).</summary>
        /// <remarks>The method must not return an <see cref="IStreamableResult"/>: its execution slot is released before a streamed response is written.</remarks>
        public bool IsEvmExecution { get; set; }

        public string? ResponseDescription { get; set; }

        public string? ExampleResponse { get; set; }

        /// <summary>
        /// Indicates that a successful response may carry a <c>null</c> result (for example, when the
        /// requested entity does not exist), as opposed to returning a JSON-RPC error.
        /// </summary>
        /// <remarks>Consumed by the documentation generator to flag nullable results.</remarks>
        public bool ResultCanBeNull { get; set; }
    }
}
