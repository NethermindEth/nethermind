// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class StorageChangeDecoder : IndexedChangeDecoder<StorageChange>
{
    private static StorageChangeDecoder? _instance;
    public static StorageChangeDecoder Instance => _instance ??= new();

    protected override StorageChange DecodeFields(ref Rlp.ValueDecoderContext ctx)
        => new(ctx.DecodeUShort(), ctx.DecodeUInt256());

    protected override void EncodeValue(RlpStream stream, StorageChange item)
        => stream.Encode(item.NewValue);

    protected override int GetValueLength(StorageChange item)
        => Rlp.LengthOf(item.NewValue);
}
