// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IGetInclusionListHandler
{
    ResultWrapper<byte[][]> Handle();
}

/// <summary>
/// EIP-7805 <c>engine_getInclusionListV1</c>: builds an inclusion list from the local mempool —
/// the best pending transaction of each sender, greedily packed up to
/// <see cref="Eip7805Constants.MaxBytesPerInclusionList"/> bytes of network-form encodings.
/// </summary>
public class GetInclusionListHandler(ITxPool txPool) : IGetInclusionListHandler
{
    public ResultWrapper<byte[][]> Handle()
    {
        List<byte[]> inclusionList = [];
        int totalBytes = 0;

        foreach (Transaction tx in txPool.GetBestTxOfEachSender())
        {
            // Blob transactions are not inclusion list candidates.
            if (tx.SupportsBlobs) continue;

            byte[] encoded = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
            if (totalBytes + encoded.Length > Eip7805Constants.MaxBytesPerInclusionList) continue;

            inclusionList.Add(encoded);
            totalBytes += encoded.Length;
        }

        return ResultWrapper<byte[][]>.Success(inclusionList.ToArray());
    }
}
