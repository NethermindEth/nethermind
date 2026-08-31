// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
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
/// as-is; only <c>validator</c> stays a byte string in both.
/// </remarks>
public sealed class XdcSubnetBlockForRpc : BlockForRpc
{
    public XdcSubnetBlockForRpc(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false)
        : base(block, includeFullTransactionData, specProvider, skipTxs)
    {
        XdcSubnetBlockHeader header = (XdcSubnetBlockHeader)block.Header;
        Validator = header.Validator ?? [];
        Validators = Unpack(header.ValidatorsAddress);
        NextValidators = Unpack(header.NextValidatorsAddress);
        Penalties = Unpack(header.PenaltiesAddress);
    }

    public byte[] Validator { get; set; }
    public Address[] Validators { get; set; }
    public Address[] NextValidators { get; set; }
    public Address[] Penalties { get; set; }

    /// <summary>Empty rather than null for a list the header omits, matching the reference client.</summary>
    internal static Address[] Unpack(ImmutableArray<Address>? addresses) => addresses is { } a ? [.. a] : [];
}

/// <summary><inheritdoc cref="XdcSubnetBlockForRpc"/></summary>
public sealed class XdcSubnetBlockHeaderForRpc(XdcSubnetBlockHeader header, ISpecProvider? specProvider = null)
    : BlockHeaderForRpc(header, specProvider)
{
    public byte[] Validator { get; set; } = header.Validator ?? [];
    public Address[] Validators { get; set; } = XdcSubnetBlockForRpc.Unpack(header.ValidatorsAddress);
    public Address[] NextValidators { get; set; } = XdcSubnetBlockForRpc.Unpack(header.NextValidatorsAddress);
    public Address[] Penalties { get; set; } = XdcSubnetBlockForRpc.Unpack(header.PenaltiesAddress);
}
