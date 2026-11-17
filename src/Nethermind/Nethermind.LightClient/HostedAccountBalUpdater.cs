// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.LightClient;

internal sealed class HostedAccountBalUpdater(IExecutionStateSource execution, ILogger logger)
{
    private const int MaxBlocks = 256;
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromSeconds(30);

    internal async Task<HostedStorageSnapshot?> TryUpdateAsync(
        VerifiedHead previousHead, VerifiedHead targetHead, Address address,
        HostedStorageSnapshot previousStorage, Hash256 targetStorageRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (targetHead.Number <= previousHead.Number || targetHead.Number - previousHead.Number > MaxBlocks)
            return null;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UpdateTimeout);
        CancellationToken token = deadline.Token;
        try
        {
            BlockHeader[] headers = new BlockHeader[(int)(targetHead.Number - previousHead.Number)];
            Hash256 expectedHash = targetHead.BlockHash;
            for (int i = headers.Length - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                BlockHeader header = i == headers.Length - 1
                    ? await execution.GetHeaderAsync(targetHead, token)
                    : await execution.GetHeaderByHashAsync(expectedHash, token);
                if (header.Number != previousHead.Number + (ulong)i + 1 ||
                    header.CalculateHash() != expectedHash ||
                    (header.Hash is not null && header.Hash != expectedHash) ||
                    header.ParentHash is null || header.BlockAccessListHash is null ||
                    (i == headers.Length - 1 && header.StateRoot != targetHead.StateRoot))
                    throw new InvalidDataException("The BAL update headers do not match the authenticated chain.");

                headers[i] = header;
                expectedHash = header.ParentHash;
            }
            if (expectedHash != previousHead.BlockHash)
                throw new InvalidDataException("The BAL update does not descend from the hosted snapshot.");

            Dictionary<ValueHash256, UInt256> changes = [];
            foreach (BlockHeader header in headers)
            {
                ReadOnlyBlockAccessList accessList = await execution.GetBlockAccessListAsync(header, token);
                if (accessList.WireHash is not null && accessList.WireHash != header.BlockAccessListHash)
                    throw new InvalidDataException("The BAL update does not match its authenticated commitment.");

                ReadOnlyAccountChanges? account = accessList.GetAccountChanges(address);
                if (account is null) continue;
                ApplyStorageChanges(changes, account, token);
            }

            return previousStorage.Apply(changes, targetStorageRoot, token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (logger.IsDebug)
                logger.Debug($"Could not advance hosted account {address} with BALs from block {previousHead.Number} to {targetHead.Number}; trying a trie-node update: {exception.Message}");
            return null;
        }
    }

    private static void ApplyStorageChanges(Dictionary<ValueHash256, UInt256> changes,
        ReadOnlyAccountChanges account, CancellationToken cancellationToken)
    {
        Span<byte> keyBytes = stackalloc byte[32];
        foreach (ReadOnlySlotChanges slot in account.StorageChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Changes.Length == 0)
                throw new InvalidDataException("A BAL storage change has no final value.");
            slot.Key.ToBigEndian(keyBytes);
            ValueHash256 path = ValueKeccak.Compute(keyBytes);
            UInt256 value = slot.Changes[^1].Value;
            changes[path] = value;
        }
    }
}
