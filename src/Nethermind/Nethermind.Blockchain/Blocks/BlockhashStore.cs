// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Consensus.Test")]
[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Blockchain.Blocks;

public class BlockhashStore(IWorldState worldState) : IBlockhashStore, IHasAccessList
{

    public void ApplyBlockhashStateChanges(BlockHeader blockHeader, IReleaseSpec spec)
    {
        if (!TryGetParentHashCell(blockHeader, spec, out StorageCell blockHashStoreCell)) return;

        worldState.Set(blockHashStoreCell, blockHeader.ParentHash!.ToUInt256());
        worldState.RecordBytecodeAccess(blockHashStoreCell.Address);
    }

    public AccessList? GetAccessList(Block block, IReleaseSpec spec) =>
        TryGetParentHashCell(block.Header, spec, out StorageCell blockHashStoreCell)
            ? AccessList.ForSingleStorageCell(in blockHashStoreCell)
            : null;

    /// <summary>
    /// Resolves the EIP-2935 history contract that must record <paramref name="header"/>'s parent hash.
    /// </summary>
    /// <returns><c>false</c> when the block records no parent hash: EIP-2935 is off, the block is genesis, or no contract is deployed.</returns>
    public bool TryGetHistoryContract(BlockHeader header, IReleaseSpec spec, [NotNullWhen(true)] out Address? historyContract)
    {
        historyContract = null;
        if (!spec.IsEip2935Enabled || header.IsGenesis || header.ParentHash is null) return false;

        Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        if (!worldState.IsContract(eip2935Account)) return false;

        historyContract = eip2935Account;
        return true;
    }

    private bool TryGetParentHashCell(BlockHeader header, IReleaseSpec spec, out StorageCell blockHashStoreCell)
    {
        if (!TryGetHistoryContract(header, spec, out Address? eip2935Account))
        {
            blockHashStoreCell = default;
            return false;
        }

        blockHashStoreCell = new StorageCell(eip2935Account, new UInt256((ulong)(header.Number - 1) % spec.Eip2935RingBufferSize));
        return true;
    }

    public Hash256? GetBlockHashFromState(BlockHeader currentHeader, ulong requiredBlockNumber, IReleaseSpec spec)
    {
        if (!TryGetHistoryCell(currentHeader, requiredBlockNumber, spec, out StorageCell blockHashStoreCell))
        {
            return null;
        }

        worldState.Get(blockHashStoreCell, out UInt256 data);
        return data.IsZero ? null : new Hash256(data.ToBigEndian());
    }

    /// <inheritdoc/>
    public bool TryGetBlockHashFromState(BlockHeader currentHeader, ulong requiredBlockNumber, IReleaseSpec spec, Span<byte> destination)
    {
        if (!TryGetHistoryCell(currentHeader, requiredBlockNumber, spec, out StorageCell blockHashStoreCell))
        {
            return false;
        }

        worldState.Get(blockHashStoreCell, out UInt256 data);
        if (data.IsZero) return false;

        data.ToBigEndian(destination);
        return true;
    }

    private static bool TryGetHistoryCell(BlockHeader currentHeader, ulong requiredBlockNumber, IReleaseSpec spec, out StorageCell blockHashStoreCell)
    {
        if (requiredBlockNumber >= currentHeader.Number ||
            requiredBlockNumber + spec.Eip2935RingBufferSize < currentHeader.Number)
        {
            blockHashStoreCell = default;
            return false;
        }

        UInt256 blockIndex = new(requiredBlockNumber % spec.Eip2935RingBufferSize);
        Address eip2935Account = spec.Eip2935ContractAddress ?? Eip2935Constants.BlockHashHistoryAddress;
        blockHashStoreCell = new StorageCell(eip2935Account, blockIndex);
        return true;
    }
}
