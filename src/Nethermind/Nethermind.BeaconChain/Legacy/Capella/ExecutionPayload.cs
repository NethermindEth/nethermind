// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Legacy.Capella;

/// <summary>Capella <c>ExecutionPayload</c>.</summary>
[SszContainer]
public partial class ExecutionPayload : Nethermind.BeaconChain.Types.ExecutionPayload
{
    [SszIgnore]
    public new ulong BlobGasUsed { get => base.BlobGasUsed; set => base.BlobGasUsed = value; }
    [SszIgnore]
    public new ulong ExcessBlobGas { get => base.ExcessBlobGas; set => base.ExcessBlobGas = value; }
}

/// <summary>Capella <c>ExecutionPayloadHeader</c>.</summary>
[SszContainer]
public partial class ExecutionPayloadHeader : Nethermind.BeaconChain.Types.ExecutionPayloadHeader
{
    [SszIgnore]
    public new ulong BlobGasUsed { get => base.BlobGasUsed; set => base.BlobGasUsed = value; }
    [SszIgnore]
    public new ulong ExcessBlobGas { get => base.ExcessBlobGas; set => base.ExcessBlobGas = value; }
}
