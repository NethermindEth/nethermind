// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merkleization;

public ref struct Merkleizer
{
    public bool IsKthBitSet(int k)
    {
        return (_filled & ((ulong)1 << k)) != 0;
    }

    public void SetKthBit(int k)
    {
        _filled |= (ulong)1 << k;
    }

    public void UnsetKthBit(int k)
    {
        _filled &= ~((ulong)1 << k);
    }

    private Span<UInt256> _chunks;
    private ulong _filled;

    public UInt256 PartChunk
    {
        get
        {
            _chunks[^1] = UInt256.Zero;
            return _chunks[^1];
        }
    }

    public Merkleizer(Span<UInt256> chunks)
    {
        _chunks = chunks;
        _filled = 0;
    }

    public Merkleizer(int depth)
    {
        _chunks = new UInt256[depth + 1];
        _filled = 0;
    }

    public void Feed(UInt256 chunk)
    {
        FeedAtLevel(chunk, 0);
    }

    public void Feed(Span<byte> bytes)
    {
        FeedAtLevel(MemoryMarshal.Cast<byte, UInt256>(bytes)[0], 0);
    }

    public void Feed(bool value)
    {
        Merkle.Ize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(uint value)
    {
        Merkle.Ize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(ulong value)
    {
        Merkle.Ize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void Feed(byte[]? value)
    {
        if (value is null)
        {
            return;
        }

        Merkle.Ize(out _chunks[^1], value);
        Feed(_chunks[^1]);
    }

    public void FeedBits(byte[]? value, uint limit)
    {
        if (value is null)
        {
            return;
        }

        Merkle.IzeBits(out _chunks[^1], value, limit);
        Feed(_chunks[^1]);
    }

    public void FeedBitvector(BitArray bitArray)
    {
        // bitfield_bytes
        byte[] bytes = new byte[(bitArray.Length + 7) / 8];
        bitArray.CopyTo(bytes, 0);

        Merkle.Ize(out _chunks[^1], bytes);
        Feed(_chunks[^1]);
    }

    public void FeedBitlist(BitArray bitArray, ulong maximumBitlistLength)
    {
        // chunk count
        ulong chunkCount = (maximumBitlistLength + 255) / 256;

        // bitfield_bytes
        byte[] bytes = new byte[(bitArray.Length + 7) / 8];
        bitArray.CopyTo(bytes, 0);

        Merkle.Ize(out _chunks[^1], bytes, chunkCount);
        Merkle.MixIn(ref _chunks[^1], bitArray.Length);
        Feed(_chunks[^1]);
    }

    //public void Feed(BlsPublicKey? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value.Bytes);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(BlsSignature? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value.Bytes);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(ValidatorIndex value)
    //{
    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<ProposerSlashing> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<AttesterSlashing> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<Validator> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<Attestation> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<PendingAttestation> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<SignedVoluntaryExit> value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Eth1Data[]? value, uint maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Length];
    //    for (int i = 0; i < value.Length; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Length);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IReadOnlyList<Deposit> value, uint maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(ValidatorIndex[]? value, uint maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<ValidatorIndex, ulong>(value.AsSpan()), maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Length);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Gwei[]? value, ulong maxLength)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<Gwei, ulong>(value.AsSpan()), maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Length);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Gwei[]? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], MemoryMarshal.Cast<Gwei, ulong>(value.AsSpan()));
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(CommitteeIndex value)
    //{
    //    Merkle.Ize(out _chunks[^1], value.Number);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Epoch value)
    //{
    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Fork? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Eth1Data? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Checkpoint? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(BeaconBlockHeader value)
    //{
    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(SignedBeaconBlockHeader value)
    //{
    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(BeaconBlockBody? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(VoluntaryExit value)
    //{
    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(AttestationData? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(IndexedAttestation? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(DepositData? value)
    //{
    //    if (value is null)
    //    {
    //        return;
    //    }

    //    Merkle.Ize(out _chunks[^1], value);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Ref<DepositData> value)
    //{
    //    if (value.Root is null)
    //    {
    //        if (value.Item == null)
    //        {
    //            return;
    //        }

    //        Merkle.Ize(out _chunks[^1], value);
    //        value.Root = new Root(_chunks[^1]);
    //        Feed(_chunks[^1]);
    //    }
    //    else
    //    {
    //        Feed(value.Root);
    //    }
    //}

    //public static UInt256 GetSubroot(DepositData depositData)
    //{
    //    Merkle.Ize(out UInt256 subRoot, depositData);
    //    return subRoot;
    //}

    // public void Feed(List<DepositData> value, ulong maxLength)
    // {
    //     Merkle.Ize(out _chunks[^1], value, maxLength);
    //     Merkle.MixIn(ref _chunks[^1], value.Count);
    //     Feed(_chunks[^1]);
    // }

    //public void Feed(List<Ref<DepositData>> value, ulong maxLength)
    //{
    //    Merkle.Ize(out _chunks[^1], value, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(List<DepositData> value, ulong maxLength)
    //{
    //    UInt256[] subRoots = new UInt256[value.Count];
    //    for (int i = 0; i < value.Count; i++)
    //    {
    //        Merkle.Ize(out subRoots[i], value[i]);
    //    }

    //    Merkle.Ize(out _chunks[^1], subRoots, maxLength);
    //    Merkle.MixIn(ref _chunks[^1], value.Count);
    //    Feed(_chunks[^1]);
    //}


    //public void Feed(ForkVersion value)
    //{
    //    Span<byte> padded = stackalloc byte[32];
    //    value.AsSpan().CopyTo(padded);
    //    Merkle.Ize(out _chunks[^1], padded);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Gwei value)
    //{
    //    Merkle.Ize(out _chunks[^1], value.Amount);
    //    Feed(_chunks[^1]);
    //}

    //public void Feed(Slot value)
    //{
    //    Merkle.Ize(out _chunks[^1], value.Number);
    //    Feed(_chunks[^1]);
    //}

    public void Feed(Bytes32 value)
    {
        // TODO: Is this going to have correct endianness? (the ulongs inside UInt256 are the correct order,
        // and if only used as memory to store bytes, the native order of a ulong (bit or little) shouldn't matter)
        Feed(MemoryMarshal.Cast<byte, UInt256>(value.AsSpan())[0]);
    }

    public void Feed(Root value)
    {
        Feed(MemoryMarshal.Cast<byte, UInt256>(value.AsSpan())[0]);
    }

    public void Feed(IReadOnlyList<Bytes32> value)
    {
        // TODO: If the above MemoryMarshal.Cast of a single Bytes32, we could use that here
        // (rather than the CreateFromLittleEndian() that wants an (unnecessarily) writeable Span.)
        // Better yet, just MemoryMarshal.Cast the entire span and pass directly to Merkle.Ize ?
        UInt256[] input = new UInt256[value.Count];
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Ize(out input[i], value[i]);
        }

        Merkle.Ize(out _chunks[^1], input);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Bytes32> value, ulong maxLength)
    {
        // TODO: If UInt256 is the correct memory layout 
        UInt256[] subRoots = new UInt256[value.Count];
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Ize(out subRoots[i], value[i]);
        }

        Merkle.Ize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Root> value)
    {
        UInt256[] input = new UInt256[value.Count];
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Ize(out input[i], value[i]);
        }

        Merkle.Ize(out _chunks[^1], input);
        Feed(_chunks[^1]);
    }

    public void Feed(IReadOnlyList<Root> value, ulong maxLength)
    {
        UInt256[] subRoots = new UInt256[value.Count];
        for (int i = 0; i < value.Count; i++)
        {
            Merkle.Ize(out subRoots[i], value[i]);
        }

        Merkle.Ize(out _chunks[^1], subRoots, maxLength);
        Merkle.MixIn(ref _chunks[^1], value.Count);
        Feed(_chunks[^1]);
    }

    private void FeedAtLevel(UInt256 chunk, int level)
    {
        for (int i = level; i < _chunks.Length; i++)
        {
            if (IsKthBitSet(i))
            {
                chunk = Merkle.HashConcatenation(_chunks[i], chunk, i);
                UnsetKthBit(i);
            }
            else
            {
                _chunks[i] = chunk;
                SetKthBit(i);
                break;
            }
        }
    }

    public UInt256 CalculateRoot()
    {
        CalculateRoot(out UInt256 result);
        return result;
    }

    public void CalculateRoot(out UInt256 root)
    {
        int lowestSet = 0;
        while (true)
        {
            for (int i = lowestSet; i < _chunks.Length; i++)
            {
                if (IsKthBitSet(i))
                {
                    lowestSet = i;
                    break;
                }
            }

            if (lowestSet == _chunks.Length - 1)
            {
                break;
            }

            UInt256 chunk = Merkle.ZeroHashes[lowestSet];
            FeedAtLevel(chunk, lowestSet);
        }

        root = _chunks[^1];
    }
}
