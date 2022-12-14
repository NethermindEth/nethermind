//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data
{
    /// <summary>
    /// A data object representing a block as being sent from the execution layer to the consensus layer.
    ///
    /// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#blobsbundlev1">BlobsBundleV1</a>
    /// </summary>
    public class BlobsBundleV1
    {
        public BlobsBundleV1()
        {
            BlockHash = Keccak.Zero;
        }

        public BlobsBundleV1(Block block)
        {
            BlockHash = block.Hash!;
            List<Memory<byte>> kzgs = new();
            List<Memory<byte>> blobs = new();
            foreach (Transaction? tx in block.Transactions)
            {
                if (tx.Type is not TxType.Blob || tx.BlobKzgs is null || tx.Blobs is null)
                {
                    continue;
                }

                for (int cc = 0, bc = 0;
                     cc < tx.BlobKzgs.Length;
                     cc += Ckzg.Ckzg.BytesPerCommitment, bc += Ckzg.Ckzg.BytesPerBlob)
                {
                    kzgs.Add(tx.BlobKzgs.AsMemory(cc, cc + Ckzg.Ckzg.BytesPerCommitment));
                    blobs.Add(tx.Blobs.AsMemory(bc, bc + Ckzg.Ckzg.BytesPerBlob));
                }
            }

            Kzgs = kzgs.ToArray();
            Blobs = blobs.ToArray();
        }

        public Memory<byte>[] Kzgs { get; set; } = Array.Empty<Memory<byte>>();
        public Memory<byte>[] Blobs { get; set; } = Array.Empty<Memory<byte>>();

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? BlockHash { get; set; } = null!;
    }
}
