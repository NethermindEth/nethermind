// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class XdcSealer(ISigner signer, IHeaderDecoder haederDecoder, ILogManager logManager) : ISealer
{
    private readonly ILogger _logger = logManager.GetClassLogger<XdcSealer>();
    public Address Address => signer.Address;

internal class XdcSealer(ISigner signer, IHeaderDecoder headerDecoder, ILogManager logManager) : ISealer
{
private readonly ILogger _logger = logManager.GetClassLogger<XdcSealer>();
public Address Address => signer.Address;

@@ -30,7 +28,7 @@ public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
if (block.IsGenesis) throw new InvalidOperationException("Can't sign genesis block");

KeccakRlpWriter writer = new();
        headerDecoder.Encode(ref writer, xdcBlockHeader, RlpBehaviors.ForSealing);
        ValueHash256 hash = writer.GetValueHash();
        if (!signer.TrySign(in hash, out Signature signature))
        {
            if (_logger.IsWarn) _logger.Warn($"XDC signer {signer.Address} could not sign block {block.Number} — skipping seal.");
            return Task.FromResult<Block>(null);
        }
        xdcBlockHeader.Validator = signature.BytesWithRecovery;

        xdcBlockHeader.Hash = xdcBlockHeader.CalculateHash().ToHash256();
        return Task.FromResult(block);
    }
}
