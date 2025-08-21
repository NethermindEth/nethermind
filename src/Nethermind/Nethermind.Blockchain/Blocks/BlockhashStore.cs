// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore(ISpecProvider specProvider, IWorldState worldState)
    : IBlockhashStore
{
    private static readonly byte[] EmptyBytes = [0];

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, ITxTracer? tracer = null)
        => ApplyBlockhashStateChanges(blockHeader, specProvider.GetSpec(blockHeader), tracer);

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec, ITxTracer? tracer = null)
    {
        if (!spec.IsEip2935Enabled || blockHeader.IsGenesis || blockHeader.ParentHash is null) return;

        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!worldState.IsContract(eip2935Account)) return;

        Hash256 parentBlockHash = blockHeader.ParentHash;
        var parentBlockIndex = new UInt256((ulong)((blockHeader.Number - 1) % Eip2935Constants.RingBufferSize));
        StorageCell blockHashStoreCell = new(eip2935Account, parentBlockIndex);
        byte[] newValue = parentBlockHash!.Bytes.WithoutLeadingZeros().ToArray();
        worldState.Set(blockHashStoreCell, newValue);
        if (tracer is not null && tracer.IsTracingStorage)
        {
            tracer.ReportStorageChange(blockHashStoreCell, null, newValue);
        }
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, long requiredBlockNumber)
        => GetBlockHashFromState(currentHeader, requiredBlockNumber, specProvider.GetSpec(currentHeader));

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, long requiredBlockNumber, IReleaseSpec? spec)
    {
        if (requiredBlockNumber >= currentHeader.Number ||
            requiredBlockNumber + Eip2935Constants.RingBufferSize < currentHeader.Number)
        {
            return null;
        }
        var blockIndex = new UInt256((ulong)(requiredBlockNumber % Eip2935Constants.RingBufferSize));
        Address? eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        StorageCell blockHashStoreCell = new(eip2935Account, blockIndex);
        ReadOnlySpan<byte> data = worldState.Get(blockHashStoreCell);
        return data.SequenceEqual(EmptyBytes) ? null : Hash256.FromBytesWithPadding(data);
    }
}
