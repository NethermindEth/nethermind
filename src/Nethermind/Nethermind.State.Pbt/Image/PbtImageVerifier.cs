// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Anchor information obtained from the consumer's chain, never from an artifact.</summary>
/// <param name="MaxBufferedCodeBytes">Local byte budget for whole code plus its 32-byte chunk encoding;
/// not a deployment-code limit. Exhaustion is retryable resource unavailability, not invalid state.</param>
/// <param name="ActivationTimestamp">The chain specification's binaryTrieTime, or null when it schedules none.
/// An anchor must precede it; there is nothing to precede when it is null.</param>
internal sealed record PbtImageAnchor(string ChainId, Hash256 GenesisHash, BlockHeader Header,
    ulong? ActivationTimestamp, int MaxBufferedCodeBytes);

/// <summary>Local buffering budget exhausted; this does not classify an artifact as invalid.</summary>
internal sealed class PbtImageResourceLimitException(string message) : Exception(message);

/// <summary>Verifies staged EIP-8347 state against the anchor's MPT root through the preimages.</summary>
/// <remarks>
/// The preimages list the anchor's addresses and slot keys in Keccak path order, which is the MPT's own order, so
/// the walk reads every listed account and slot back from the staged state and folds them straight into the MPT
/// without sorting. The staged state is trusted only for what the walk reads: a listed entry the state lacks fails
/// here, while the caller compares the listed counts with the staged ones to refuse state the preimages never name.
/// </remarks>
internal static class PbtImageVerifier
{
    private const string VerifyPhase = "PBT verify preimages";

    /// <returns>The accounts and slots the preimages list.</returns>
    public static (ulong Accounts, ulong Slots) Verify(Stream preimages, IPbtPersistence.IReader state,
        PbtImageAnchor anchor, ILogManager logManager, CancellationToken cancellationToken = default)
    {
        ValidateAnchor(anchor);
        ILogger logger = logManager.GetClassLogger(typeof(PbtImageVerifier));
        Stopwatch verifying = Stopwatch.StartNew();
        ulong accounts = 0;
        ulong slots = 0;
        ValueHash256 mptRoot;
        using (ProgressReporter progress = PbtImageProgress.Start(VerifyPhase, "acc", 0, logManager))
        {
            progress.Logger.SetFormat(p => $"{PbtImageProgress.Format(VerifyPhase, "acc", p)} | {slots,15:N0} slot");
            mptRoot = MptRightmostNodeStore.CalculateRoot(Accounts(), MptRightmostNodeStore.DefaultWindowSize, cancellationToken);

            IEnumerable<KeyValuePair<ValueHash256, byte[]>> Accounts()
            {
                PbtPreimageReader reader = new(preimages);
                while (reader.ReadAccount(out Address? address, out uint slotCount, cancellationToken))
                {
                    progress.Update(++accounts);
                    Address accountAddress = address!;
                    Account account = state.GetAccount(PbtKeyDerivation.AddressKeyHash(accountAddress))
                        ?? throw new InvalidDataException($"Preimages list the account {accountAddress} the snapshot lacks.");
                    ValueHash256 storageRoot = MptRightmostNodeStore.CalculateRoot(Storage(), MptRightmostNodeStore.DefaultWindowSize, cancellationToken);
                    yield return new(ValueKeccak.Compute(accountAddress.Bytes),
                        AccountDecoder.Instance.Encode(account.WithChangedStorageRoot(storageRoot.ToHash256())).Bytes);

                    IEnumerable<KeyValuePair<ValueHash256, byte[]>> Storage()
                    {
                        for (uint index = 0; index < slotCount; index++)
                        {
                            slots++;
                            ValueHash256 slot = reader.ReadSlot(cancellationToken);
                            PbtStorageTreeKey key = PbtStateKey.Storage(accountAddress, new UInt256(slot.Bytes, isBigEndian: true));
                            ISlotRun run = state.GetSlotRun(SlotRun.RunKey(key));
                            EvmWord value = run.Get(SlotRun.IndexOf(key));
                            SlotRun.Return(run);
                            if (EvmWordSlot.IsZero(value))
                                throw new InvalidDataException($"Preimages list a slot of {accountAddress} the snapshot lacks.");
                            yield return new(ValueKeccak.Compute(slot.Bytes),
                                Rlp.Encode(new UInt256(EvmWordSlot.AsReadOnlySpan(in value), isBigEndian: true)).Bytes);
                        }
                    }
                }
            }
        }

        if (mptRoot != anchor.Header.StateRoot!.ValueHash256)
            throw new InvalidDataException("Snapshot does not reproduce the anchor MPT root.");
        if (logger.IsInfo)
            logger.Info($"PBT verified {accounts:N0} accounts and {slots:N0} slots against the MPT root {mptRoot} in {verifying.Elapsed:hh\\:mm\\:ss}.");
        return (accounts, slots);
    }

    public static void ValidateAnchor(PbtImageAnchor anchor)
    {
        BlockHeader header = anchor.Header;
        if (anchor.ActivationTimestamp is { } activation && header.Timestamp >= activation || header.Hash is null || header.StateRoot is null)
            throw new InvalidDataException("Image requires a pre-activation MPT anchor.");
        ArgumentOutOfRangeException.ThrowIfNegative(anchor.MaxBufferedCodeBytes);
    }
}
