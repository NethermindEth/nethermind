// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceSerializer
{
    unsafe List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized);
    List<ParityLikeTxTrace>? Deserialize(Stream serialized);
    byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces);
}
