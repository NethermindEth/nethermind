// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class XdcSealer(ISigner signer) : ISealer
{
    private static XdcHeaderDecoder _xdcHeaderDecoder = new XdcHeaderDecoder();
    public Address Address => signer.Address;

    public bool CanSeal(long blockNumber, Hash256 parentHash)
    {
        //We might want to add more logic here in the future
        return true;
    }

    public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
    {
        if (block.Header is not XdcBlockHeader xdcBlockHeader)
            throw new ArgumentException("Only XDC headers are supported.");
        if (block.IsGenesis) throw new InvalidOperationException("Can't sign genesis block");

        KeccakRlpStream hashStream = new KeccakRlpStream();
        _xdcHeaderDecoder.Encode(hashStream, xdcBlockHeader, RlpBehaviors.ForSealing);
        xdcBlockHeader.Validator = signer.Sign(hashStream.GetValueHash()).BytesWithRecovery;
        return Task.FromResult(block);
    }
}
