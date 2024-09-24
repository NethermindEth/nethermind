// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Data;

public class GetBlobsV1Result(IEnumerable<BlobAndProofV1?> blobsAndProofs)
{
    public IEnumerable<BlobAndProofV1?> BlobsAndProofs { get; } = blobsAndProofs;
}
