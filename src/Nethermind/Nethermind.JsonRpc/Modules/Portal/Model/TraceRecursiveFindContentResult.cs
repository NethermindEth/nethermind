// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Portal;

public class TraceRecursiveFindContentResult
{
    public string Content { get; set; }
    public bool UtpTransfer { get; set; }

    public TraceResultObject Trace { get; set; }

    public class TraceResultObject
    {
        public ValueHash256 Origin { get; set; } // Local Node ID
        public ValueHash256 TargetId { get; set; } // Target content ID
        public ValueHash256? ReceivedFrom { get; set; } // Node ID from which content was received (nullable)
        public Dictionary<ValueHash256, TraceResultResponseItem> Responses { get; set; } // Contains response details
        public Dictionary<ValueHash256, TraceResultMetadataObject> Metadata { get; set; } // Metadata object for the nodes
        public long StartedAtMs { get; set; } // Timestamp of request start in milliseconds
        public List<ValueHash256> Cancelled { get; set; } // List of cancelled node IDs
    }

    public class TraceResultResponseItem
    {
        public int DurationsMs { get; set; } // Time it took for the lookup in milliseconds
        public List<ValueHash256> RespondedWith { get; set; } // List of node IDs
    }

    public class TraceResultMetadataObject
    {
        public string Enr { get; set; } // ENR (Ethereum Node Record)
        public ValueHash256 Distance { get; set; } // Distance to the node
    }
}
