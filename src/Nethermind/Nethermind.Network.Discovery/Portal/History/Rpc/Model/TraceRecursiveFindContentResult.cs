// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Portal.History.Rpc.Model;

public class TraceRecursiveFindContentResult
{
    public byte[] Content { get; set; } = null!;
    public bool UtpTransfer { get; set; }

    public TraceResultObject Trace { get; set; } = null!;

    public class TraceResultObject
    {
        public ValueHash256 Origin { get; set; } // Local Node ID
        public ValueHash256 TargetId { get; set; } // Target content ID
        public ValueHash256? ReceivedFrom { get; set; } // Node ID from which content was received (nullable)
        public Dictionary<ValueHash256, TraceResultResponseItem> Responses { get; set; } = new(); // Contains response details
        public Dictionary<ValueHash256, TraceResultMetadataObject> Metadata { get; set; } = new(); // Metadata object for the nodes
        public long StartedAtMs { get; set; } // Timestamp of request start in milliseconds
        public List<ValueHash256> Cancelled { get; set; } = new(); // List of cancelled node IDs
    }

    public class TraceResultResponseItem
    {
        public int DurationsMs { get; set; } // Time it took for the lookup in milliseconds
        public List<ValueHash256> RespondedWith { get; set; } = new(); // List of node IDs
    }

    public class TraceResultMetadataObject
    {
        public string Enr { get; set; } = null!; // ENR (Ethereum Node Record)
        public ValueHash256 Distance { get; set; } // Distance to the node
    }
}
