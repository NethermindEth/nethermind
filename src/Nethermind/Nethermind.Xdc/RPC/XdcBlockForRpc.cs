// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Xdc.RPC;

/// <summary>XDC RPC block model: adds the XDPoS seal and the masternode and penalty lists.</summary>
/// <remarks>
/// Shape follows XDPoSChain's <c>RPCMarshalHeader</c> (<c>internal/ethapi/api.go</c>): all three are
/// <c>hexutil.Bytes</c> — packed 20-byte addresses, not JSON arrays — and all three are emitted even
/// when empty. The subnet fork differs on both counts; see <see cref="XdcSubnetBlockForRpc"/>.
/// </remarks>
public sealed class XdcBlockForRpc : BlockForRpc
{
    public XdcBlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false)
        : base(block, includeFullTransactionData, specProvider, skipTxs)
    {
        XdcBlockHeader header = (XdcBlockHeader)block.Header;
        Validator = header.Validator ?? [];
        Validators = header.Validators ?? [];
        Penalties = header.Penalties ?? [];
    }

    public byte[] Validator { get; set; }
    public byte[] Validators { get; set; }
    public byte[] Penalties { get; set; }
}

/// <summary><inheritdoc cref="XdcBlockForRpc"/></summary>
public sealed class XdcBlockHeaderForRpc(XdcBlockHeader header, ISpecProvider? specProvider = null)
    : BlockHeaderForRpc(header, specProvider)
{
    public byte[] Validator { get; set; } = header.Validator ?? [];
    public byte[] Validators { get; set; } = header.Validators ?? [];
    public byte[] Penalties { get; set; } = header.Penalties ?? [];
}

/// <summary>Produces the XDC RPC block/header models.</summary>
/// <remarks>
/// Subnet is matched before mainnet because <see cref="XdcSubnetBlockHeader"/> derives from
/// <see cref="XdcBlockHeader"/>; the reverse order would give subnet blocks the mainnet shape.
/// </remarks>
public sealed class XdcBlockForRpcFactory : BlockForRpcFactory
{
    public override BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        block.Header switch
        {
            XdcSubnetBlockHeader => new XdcSubnetBlockForRpc(block, includeFullTransactionData, specProvider, skipTxs),
            XdcBlockHeader => new XdcBlockForRpc(block, includeFullTransactionData, specProvider, skipTxs),
            _ => base.Create(block, includeFullTransactionData, specProvider, skipTxs)
        };

    public override BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        header switch
        {
            XdcSubnetBlockHeader subnet => new XdcSubnetBlockHeaderForRpc(subnet, specProvider),
            XdcBlockHeader xdc => new XdcBlockHeaderForRpc(xdc, specProvider),
            _ => base.CreateHeader(header, specProvider)
        };
}
