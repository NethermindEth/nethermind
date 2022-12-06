// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.TraceStore;

public interface ITraceSerializer<TTrace>
{
    unsafe List<TTrace>? Deserialize(Span<byte> serialized);
    List<TTrace>? Deserialize(Stream serialized);
    byte[] Serialize(IReadOnlyCollection<TTrace> traces);
}
