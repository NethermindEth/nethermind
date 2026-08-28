// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageChangeDecoder : IndexedChangeDecoder<StorageChange>
{
    public static readonly StorageChangeDecoder Instance = new();

    protected override StorageChange DecodeFields(ref RlpReader ctx)
        => new(ctx.DecodeUInt(), ctx.DecodeEvmWord());

    protected override void EncodeValue<TWriter>(ref TWriter writer, StorageChange item)
        => writer.Encode(item.Value);

    protected override int GetValueLength(StorageChange item)
        => Rlp.LengthOf(item.Value);
}
