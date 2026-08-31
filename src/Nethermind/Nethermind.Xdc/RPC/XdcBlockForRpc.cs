// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// XDC-flavoured RPC block model: adds the XDPoS seal (<c>validator</c>) and the masternode
/// (<c>validators</c>) and penalty (<c>penalties</c>) lists the base model has no place for.
/// </summary>
/// <remarks>
/// Field names and encodings follow XDPoSChain's <c>RPCMarshalHeader</c> (<c>internal/ethapi/api.go</c>),
/// which emits all three as <c>hexutil.Bytes</c> — packed 20-byte addresses rather than JSON arrays —
/// on every header, empty ones included. The subnet fork encodes the lists as address arrays and adds
/// <c>nextValidators</c>; see <see cref="XdcSubnetBlockForRpc"/>.
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

/// <summary>
/// Produces the XDC RPC block/header models, picking the subnet shape for subnet headers and falling
/// back to the base (seal-agnostic) models for anything that isn't an XDC header.
/// </summary>
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
