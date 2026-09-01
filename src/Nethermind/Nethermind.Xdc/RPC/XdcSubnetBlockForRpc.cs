// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;

namespace Nethermind.Xdc.RPC;

/// <summary>
/// Subnet-flavoured RPC block model: the same XDPoS seal as <see cref="XdcBlockForRpc"/>, but with the
/// masternode and penalty lists spelled out as address arrays and the extra <c>nextValidators</c> list.
/// </summary>
/// <remarks>
/// The subnet fork's header carries <c>[]common.Address</c> where mainnet packs the addresses into a
/// byte string, and its <c>RPCMarshalHeader</c> (XDC-Subnet <c>internal/ethapi/api.go</c>) marshals them
/// as-is; only <c>validator</c> stays a byte string in both. The lists are unpacked from the raw header
/// bytes rather than through the headers' cached <c>...Address</c> projections, whose lazy
/// <see cref="System.Nullable{T}"/> write would not be atomic under concurrent RPC reads.
/// </remarks>
public sealed class XdcSubnetBlockForRpc : BlockForRpc
{
    public XdcSubnetBlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false)
        : base(block, includeFullTransactionData, specProvider, skipTxs)
    {
        XdcSubnetBlockHeader header = (XdcSubnetBlockHeader)block.Header;
        Validator = header.Validator ?? [];
        Validators = XdcExtensions.ExtractAddresses(header.Validators) ?? [];
        NextValidators = XdcExtensions.ExtractAddresses(header.NextValidators) ?? [];
        Penalties = XdcExtensions.ExtractAddresses(header.Penalties) ?? [];
    }

    public byte[] Validator { get; set; }
    public Address[] Validators { get; set; }
    public Address[] NextValidators { get; set; }
    public Address[] Penalties { get; set; }
}

/// <summary><inheritdoc cref="XdcSubnetBlockForRpc"/></summary>
public sealed class XdcSubnetBlockHeaderForRpc(XdcSubnetBlockHeader header, ISpecProvider? specProvider = null)
    : BlockHeaderForRpc(header, specProvider)
{
    public byte[] Validator { get; set; } = header.Validator ?? [];
    public Address[] Validators { get; set; } = XdcExtensions.ExtractAddresses(header.Validators) ?? [];
    public Address[] NextValidators { get; set; } = XdcExtensions.ExtractAddresses(header.NextValidators) ?? [];
    public Address[] Penalties { get; set; } = XdcExtensions.ExtractAddresses(header.Penalties) ?? [];
}
