// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Persistence.PaprikaFlat;

/// <summary>The flat writes of a <see cref="Db.FlatLayout.PaprikaFlat"/> write batch, held in <paramref name="buffer"/> until it commits.</summary>
/// <remarks>
/// Slots are stored RLP-wrapped, byte for byte as the RLP-encoded flat layouts store them; a zero slot is stored as the
/// non-empty RLP of its one-byte value, so it reads back as present.
/// </remarks>
internal readonly struct PaprikaFlatWriteBatch(PaprikaWriteBuffer buffer) : BasePersistence.IHashedFlatWriteBatch
{
    /// <inheritdoc/>
    public void SelfDestruct(in ValueHash256 address) => buffer.SelfDestruct(address);

    /// <inheritdoc/>
    public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath) => buffer.SelfDestructRange(fromPath, toPath);

    /// <inheritdoc/>
    public void RemoveAccount(in ValueHash256 address) => buffer.RemoveAccount(address);

    /// <inheritdoc/>
    public void SetAccount(in ValueHash256 address, ReadOnlySpan<byte> value) => buffer.SetAccount(address, value);

    /// <inheritdoc/>
    [SkipLocalsInit]
    public void SetStorage(in ValueHash256 address, in ValueHash256 slotHash, in UInt256? value)
    {
        if (value is null)
        {
            buffer.RemoveSlot(address, slotHash);
            return;
        }

        Span<byte> encoded = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        int length = BaseFlatPersistence.EncodeSlotValue(value.Value, rlpWrapSlots: true, encoded);
        buffer.SetSlot(address, slotHash, encoded[..length]);
    }

    /// <inheritdoc/>
    /// <remarks>Stored verbatim once decoding validates it and bounds the item, trimming any trailing bytes.</remarks>
    public void SetStorageEncoded(in ValueHash256 address, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
    {
        RlpReader reader = new(rlpValue);
        reader.DecodeByteArraySpan();
        buffer.SetSlot(address, slotHash, rlpValue[..reader.Position]);
    }

    /// <inheritdoc/>
    public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => buffer.DeleteAccountRange(fromPath, toPath);

    /// <inheritdoc/>
    public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
        buffer.DeleteStorageRange(addressHash, fromPath, toPath);
}
