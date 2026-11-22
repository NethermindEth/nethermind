// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient;

internal sealed record EezSettlement(Address Registry, ulong RollupId, ulong ChainId);

internal sealed record EezCommittedHead(VerifiedHead Head, VerifiedHead SettlementHead, BlockHeader Header);

internal static class EezCommitment
{
    internal const ulong RollupsMappingSlot = 2;
    internal const ulong RootFieldOffset = 1;
    private static readonly HeaderDecoder _headerDecoder = new();

    internal static UInt256 RootSlot(ulong rollupId)
    {
        Span<byte> preimage = stackalloc byte[64];
        new UInt256(rollupId).ToBigEndian(preimage[..32]);
        new UInt256(RollupsMappingSlot).ToBigEndian(preimage[32..]);
        UInt256 mappingBase = new(ValueKeccak.Compute(preimage).Bytes, isBigEndian: true);
        UInt256.Add(mappingBase, RootFieldOffset, out UInt256 slot);
        return slot;
    }

    internal static async Task<EezCommittedHead?> ResolveAsync(IExecutionStateSource settlementSource,
        IExecutionStateSource rollupSource, EezSettlement settlement, VerifiedHead settlementHead,
        CancellationToken cancellationToken)
    {
        Account registry = await settlementSource.GetAccountAsync(settlementHead, settlement.Registry, cancellationToken);
        if (!registry.HasStorage) return null;
        UInt256 commitment = await settlementSource.GetStorageAsync(settlementHead, settlement.Registry, registry,
            RootSlot(settlement.RollupId), cancellationToken);
        if (commitment.IsZero) return null;

        Hash256 hash = new(commitment.ToBigEndian());
        BlockHeader header = await rollupSource.GetHeaderByHashAsync(hash, cancellationToken);
        Verify(header, hash);
        return new EezCommittedHead(new VerifiedHead(settlementHead.Slot, header.Number, hash, header.StateRoot!),
            settlementHead, header);
    }

    internal static void Verify(BlockHeader header, Hash256 commitment)
    {
        if (header.Hash != commitment || header.StateRoot is null
            || _headerDecoder.GetLength(header, RlpBehaviors.None) > FinalizedHeaderHistory.MaxHeaderBytes
            || header.CalculateHash() != commitment)
            throw new InvalidDataException("Rollup header differs from the authenticated EEZ settlement commitment.");
    }
}
