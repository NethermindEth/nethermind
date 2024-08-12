// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Merge.Plugin.Data;

public class GetBlobsV1Result(BlobAndProofV1?[] blobsAndProofs)
{
    public BlobAndProofV1?[] BlobsAndProofs = blobsAndProofs;
}
