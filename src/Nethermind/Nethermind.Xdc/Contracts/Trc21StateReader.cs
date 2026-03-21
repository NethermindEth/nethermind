// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc.Contracts;

internal class Trc21StateReader(IStateReader stateReader, ISpecProvider specProvider) : ITrc21StateReader
{
    private const ulong IssuerTokensSlot = 1;
    private const ulong IssuerTokensStateSlot = 2;

    private const ulong TokenBalancesSlot = 0;
    private const ulong TokenMinFeeSlot = 1;

    private readonly LruCache<Hash256, Dictionary<Address, UInt256>> _capacityCache = new(128, "XDC TRC21 Fee Capacity");

    public IReadOnlyDictionary<Address, UInt256> GetFeeCapacities(XdcBlockHeader? baseBlock)
    {
        if (baseBlock?.StateRoot is null)
        {
            return LoadFeeCapacities(baseBlock);
        }

        if (!_capacityCache.TryGet(baseBlock.StateRoot, out Dictionary<Address, UInt256>? cached) || cached is null)
        {
            cached = LoadFeeCapacities(baseBlock);
            _capacityCache.Set(baseBlock.StateRoot, cached);
        }

        return cached;
    }

    public bool ValidateTransaction(XdcBlockHeader? baseBlock, Address from, Address token, ReadOnlySpan<byte> data)
    {
        if (baseBlock is null || data.IsEmpty)
        {
            return false;
        }

        UInt256 senderBalanceSlot = CalculateMappingSlot(from.ToHash(), TokenBalancesSlot);
        UInt256 balance = ReadStorage(baseBlock, token, senderBalanceSlot);

        if (!balance.IsZero)
        {
            UInt256 minFee = ReadStorage(baseBlock, token, TokenMinFeeSlot);
            UInt256 transferValue = ExtractTransferValue(data);
            return !UInt256.AddOverflow(minFee, transferValue, out UInt256 required) && balance >= required;
        }

        // When account has zero token balance, tx is accepted.
        return true;
    }

    private Dictionary<Address, UInt256> LoadFeeCapacities(XdcBlockHeader? baseBlock)
    {
        if (baseBlock is null)
        {
            return [];
        }

        IXdcReleaseSpec spec = specProvider.GetXdcSpec(baseBlock);
        Address issuerContract = spec.Trc21IssuerContract;
        var capacities = new Dictionary<Address, UInt256>();
        ulong tokenCount = (ulong)ReadStorage(baseBlock, issuerContract, IssuerTokensSlot);
        UInt256 tokenArrayBaseSlot = CalculateDynamicArrayBaseSlot(IssuerTokensSlot);
        for (ulong i = 0; i < tokenCount; i++)
        {
            UInt256 tokenArraySlot = tokenArrayBaseSlot + i;
            Address tokenAddress = ReadStorageAddress(baseBlock, issuerContract, tokenArraySlot);
            if (tokenAddress == Address.Zero)
                continue;

            UInt256 balanceSlot = CalculateMappingSlot(tokenAddress.ToHash(), IssuerTokensStateSlot);
            capacities[tokenAddress] = ReadStorage(baseBlock, issuerContract, balanceSlot);
        }

        return capacities;
    }

    private UInt256 ReadStorage(XdcBlockHeader baseBlock, Address contract, ulong slot)
    {
        return ReadStorage(baseBlock, contract, new UInt256(slot));
    }

    private UInt256 ReadStorage(XdcBlockHeader baseBlock, Address contract, in UInt256 slot)
    {
        ReadOnlySpan<byte> value = stateReader.GetStorage(baseBlock, contract, slot);
        return value.IsEmpty ? UInt256.Zero : new UInt256(value, isBigEndian: true);
    }

    private Address ReadStorageAddress(XdcBlockHeader baseBlock, Address contract, in UInt256 slot)
    {
        ReadOnlySpan<byte> value = stateReader.GetStorage(baseBlock, contract, slot);
        return value.IsEmpty ? Address.Zero : Address.FromNumber(new UInt256(value, isBigEndian: true));
    }

    private static UInt256 CalculateDynamicArrayBaseSlot(ulong slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        new UInt256(slot).ToBigEndian(slotBytes);
        return new UInt256(Keccak.Compute(slotBytes).Bytes, isBigEndian: true);
    }

    private static UInt256 CalculateMappingSlot(in ValueHash256 key, ulong slot)
    {
        Span<byte> input = stackalloc byte[64];
        key.BytesAsSpan.CopyTo(input);
        new UInt256(slot).ToBigEndian(input[32..]);
        return new UInt256(Keccak.Compute(input).Bytes, isBigEndian: true);
    }

    private static UInt256 ExtractTransferValue(ReadOnlySpan<byte> data)
    {
        if (data.Length == XdcConstants.Trc21TransferCalldataLength &&
            data[..4].SequenceEqual(XdcConstants.Trc21TransferMethod))
        {
            return new UInt256(data.Slice(36, 32), isBigEndian: true);
        }

        // XDPoSChain currently checks for 80-byte transferFrom calldata, although it should probably be 100
        // https://github.com/XinFinOrg/XDPoSChain/blob/4f599282b32cfd668bea556204fcdcf03dce2a67/core/state/trc21_reader.go#L127
        if (data.Length == XdcConstants.Trc21TransferFromCalldataLength &&
            data[..4].SequenceEqual(XdcConstants.Trc21TransferFromMethod))
        {
            return new UInt256(data[68..], isBigEndian: true);
        }

        return UInt256.Zero;
    }
}
