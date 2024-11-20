// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Verkle.Tree.TreeStore;

public readonly struct StateRootToBlockMap(IDb stateRootToBlock)
{
    public long this[Hash256 key]
    {
        get
        {
            if (key == Hash256.Zero) return -1;
            var encodedBlock = stateRootToBlock[key.Bytes];
            return encodedBlock is null ? -2 : BinaryPrimitives.ReadInt64LittleEndian(encodedBlock);
        }
        set
        {
            Span<byte> encodedBlock = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(encodedBlock, value);
            if (!stateRootToBlock.KeyExists(key.Bytes))
                stateRootToBlock.Set(key.Bytes, encodedBlock.ToArray());
        }
    }
}
