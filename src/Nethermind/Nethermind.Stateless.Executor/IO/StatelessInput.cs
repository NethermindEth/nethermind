// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial class StatelessInput<TExecutionPayload> where TExecutionPayload
    : SszExecutionPayloadV1, ISszExecutionPayloadFactory<TExecutionPayload>, ISszCodec<TExecutionPayload>, new()
{
    public NewPayloadRequest<TExecutionPayload> NewPayloadRequest { get; set; } = null!;

    public ExecutionWitness Witness { get; set; }

    public ChainConfig ChainConfig { get; set; }

    [SszList(0x8000)]
    public SszPublicKey[] PublicKeys { get; set; } = [];
}
