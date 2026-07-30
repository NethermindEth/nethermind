// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.SnapServer;

public sealed class SnapCodeServer(IReadOnlyKeyValueStore codeDb)
{
    public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > ISnapServer.HardResponseByteLimit)
            byteLimit = ISnapServer.HardResponseByteLimit;

        long currentByteCount = 0;
        using DeferredRlpItemList.Builder builder = new(requestedHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();

        foreach (ValueHash256 codeHash in requestedHashes)
        {
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                writer.WriteValue([]);
                currentByteCount += 1;
                continue;
            }

            byte[]? code = codeDb[codeHash.Bytes];
            if (code is not null)
            {
                writer.WriteValue(code);
                currentByteCount += code.Length;
            }
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
