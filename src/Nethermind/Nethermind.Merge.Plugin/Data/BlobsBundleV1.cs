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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data
{
    /// <summary>
    /// A data object representing a block as being sent from the execution layer to the consensus layer.
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#executionpayloadv1"/>
    /// </summary>
    public class BlobsBundleV1
    {
        public BlobsBundleV1()
        {
            BlockHash = Keccak.Zero;
            Kzgs = Array.Empty<byte[]>();
            Blobs = Array.Empty<byte[]>();
        }

        public BlobsBundleV1(Block block)
        {
            BlockHash = block.Hash!;
            List<byte[]> kzgs = new();
            List<byte[]> blobs = new();
            foreach (Transaction? tx in block.Transactions)
            {
                if(tx?.Type is not TxType.Blob)
                {
                    continue;
                }
                kzgs.AddRange(tx.BlobKzgs!);
                blobs.AddRange(tx.Blobs!);
            }
            Kzgs = kzgs.ToArray();
            Blobs = blobs.ToArray();
        }

        public byte[][] Kzgs { get; set; } = Array.Empty<byte[]>();
        public byte[][] Blobs { get; set; } = Array.Empty<byte[]>();

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? BlockHash { get; set; } = null!;
    }
}
