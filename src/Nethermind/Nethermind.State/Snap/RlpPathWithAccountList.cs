// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>An <see cref="IPathWithAccountList"/> over an RLP list of <c>[path, slimAccount]</c> pairs.</summary>
/// <remarks>
/// Each pair is read with <see cref="IRlpItemList.ReadRlp"/>, so a list built with
/// <see cref="DeferredRlpItemList.Builder"/> must append each pair with <see cref="DeferredRlpItemList.Builder.Writer.WriteRaw"/>.
/// </remarks>
public sealed class RlpPathWithAccountList(IRlpItemList inner) : IPathWithAccountList
{
    public int Count => inner.Count;

    public RefPathWithAccount this[int index] => new(inner.ReadRlp(index));

    public int RlpLength => inner.RlpLength;

    public void Write<TWriter>(ref TWriter writer)
        where TWriter : struct, IRlpWriteBackend, allows ref struct => inner.Write(ref writer);

    public void Dispose() => inner.Dispose();
}
