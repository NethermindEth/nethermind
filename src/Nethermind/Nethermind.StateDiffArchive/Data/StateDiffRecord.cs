// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateDiffArchive.Data;

/// <summary>How an account changed in a block: not re-set (storage-only), upserted, or deleted.</summary>
public enum AccountChangeKind : byte
{
    /// <summary>The account itself was not written this block; only its storage changed (its storage root is reconciled from the slots on replay).</summary>
    None = 0,

    /// <summary>The account was upserted to <see cref="StateDiffRecord.AccountView.Account"/>.</summary>
    Set = 1,

    /// <summary>The account was deleted (<c>writeBatch.Set(addr, null)</c>), which also clears its storage.</summary>
    Deleted = 2,
}

/// <summary>
/// A readonly, lazily-decoded view over one block's committed state diff, reading directly from the
/// (pooled) byte buffer it owns — accounts, storage, self-destructs and code are walked on demand without
/// materializing intermediate objects. Built by <see cref="StateDiffRecordBuilder"/>; see it for the wire
/// format. Replaying these writes through <c>IWorldStateScopeProvider.IScope</c> and committing rebuilds
/// the trie without the EVM.
/// </summary>
/// <remarks>
/// Owns the pooled buffer it reads from: <see cref="Dispose"/> returns it. Spans returned by the slot/code
/// views point into that buffer and are valid only until the record is disposed.
/// </remarks>
public sealed class StateDiffRecord : IDisposable
{
    public const byte CurrentVersion = 1;

    private readonly IMemoryOwner<byte> _owner;
    private readonly ReadOnlyMemory<byte> _rlp;
    private readonly int _accountsStart;
    private readonly int _accountsEnd;
    private readonly int _codesStart;
    private readonly int _codesEnd;

    public byte Version { get; }
    public ulong BlockNumber { get; }
    public Hash256 StateRoot { get; }

    public StateDiffRecord(IMemoryOwner<byte> owner, ReadOnlyMemory<byte> rlp)
    {
        _owner = owner;
        _rlp = rlp;

        RlpReader r = new(rlp.Span);
        r.ReadSequenceLength();
        Version = r.DecodeByte();
        BlockNumber = r.DecodeULong();
        StateRoot = r.DecodeKeccak() ?? throw new RlpException("StateDiffRecord.StateRoot must not be null");

        int accountsLength = r.ReadSequenceLength();
        _accountsStart = r.Position;
        _accountsEnd = r.Position + accountsLength;

        r.Position = _accountsEnd;
        int codesLength = r.ReadSequenceLength();
        _codesStart = r.Position;
        _codesEnd = r.Position + codesLength;
    }

    public bool HasCodes => _codesEnd > _codesStart;

    public AccountEnumerator Accounts => new(_rlp, _accountsStart, _accountsEnd);
    public CodeEnumerator Codes => new(_rlp, _codesStart, _codesEnd);

    public void Dispose() => _owner.Dispose();

    /// <summary>Walks the per-address change entries; reusable, allocation-free.</summary>
    public ref struct AccountEnumerator(ReadOnlyMemory<byte> rlp, int start, int end)
    {
        private int _position = start;
        private AccountView _current = default;

        public readonly AccountEnumerator GetEnumerator() => this;
        public readonly AccountView Current => _current;

        public bool MoveNext()
        {
            if (_position >= end) return false;
            RlpReader r = new(rlp.Span) { Position = _position };
            int contentLength = r.ReadSequenceLength();
            int contentStart = r.Position;
            _current = new AccountView(rlp, contentStart);
            _position = contentStart + contentLength;
            return true;
        }
    }

    /// <summary>A single per-address change: optional account upsert/delete, optional storage clear, and slots.</summary>
    public readonly ref struct AccountView
    {
        private readonly ReadOnlyMemory<byte> _rlp;
        private readonly int _accountStart; // -1 unless Change == Set
        private readonly int _slotsStart;
        private readonly int _slotsEnd;

        public Address Address { get; }
        public AccountChangeKind Change { get; }
        public bool StorageCleared { get; }

        internal AccountView(ReadOnlyMemory<byte> rlp, int contentStart)
        {
            _rlp = rlp;
            RlpReader r = new(rlp.Span) { Position = contentStart };
            Address = r.DecodeAddress() ?? throw new RlpException("AccountDiff.Address must not be null");
            Change = (AccountChangeKind)r.DecodeByte();
            if (Change == AccountChangeKind.Set)
            {
                _accountStart = r.Position;
                r.SkipItem();
            }
            else
            {
                _accountStart = -1;
            }
            StorageCleared = r.DecodeBool();
            int slotsLength = r.ReadSequenceLength();
            _slotsStart = r.Position;
            _slotsEnd = r.Position + slotsLength;
        }

        public bool HasSlots => _slotsEnd > _slotsStart;

        /// <summary>The upserted account when <see cref="Change"/> is <see cref="AccountChangeKind.Set"/>; otherwise null.</summary>
        public Account? Account
        {
            get
            {
                if (_accountStart < 0) return null;
                RlpReader r = new(_rlp.Span) { Position = _accountStart };
                return AccountDecoder.Instance.Decode(ref r);
            }
        }

        public SlotSet Slots => new(_rlp, _slotsStart, _slotsEnd);
    }

    /// <summary>
    /// A storable (non-ref) handle to an account's changed-slot region within the record buffer; enumerating
    /// it parses the slots on demand, so it can be carried into a parallel worker and read there.
    /// </summary>
    public readonly struct SlotSet(ReadOnlyMemory<byte> rlp, int start, int end)
    {
        /// <summary>Encoded byte length of the region — an O(1) proxy for how much work the slots represent (no slot count needed).</summary>
        public int Length => end - start;

        public SlotEnumerator GetEnumerator() => new(rlp, start, end);
    }

    /// <summary>Walks an account's changed storage slots.</summary>
    public ref struct SlotEnumerator(ReadOnlyMemory<byte> rlp, int start, int end)
    {
        private int _position = start;
        private SlotView _current = default;

        public readonly SlotEnumerator GetEnumerator() => this;
        public readonly SlotView Current => _current;

        public bool MoveNext()
        {
            if (_position >= end) return false;
            RlpReader r = new(rlp.Span) { Position = _position };
            int contentLength = r.ReadSequenceLength();
            int contentEnd = r.Position + contentLength;
            UInt256 index = r.DecodeUInt256();
            ReadOnlySpan<byte> value = r.DecodeByteArraySpan();
            _current = new SlotView(index, value);
            _position = contentEnd;
            return true;
        }
    }

    /// <summary>A single storage slot write; a zero/empty <see cref="Value"/> denotes a cleared slot.</summary>
    public readonly ref struct SlotView(UInt256 index, ReadOnlySpan<byte> value)
    {
        public UInt256 Index { get; } = index;
        public ReadOnlySpan<byte> Value { get; } = value;
    }

    /// <summary>Walks the contract code captured this block.</summary>
    public ref struct CodeEnumerator(ReadOnlyMemory<byte> rlp, int start, int end)
    {
        private int _position = start;
        private CodeView _current = default;

        public readonly CodeEnumerator GetEnumerator() => this;
        public readonly CodeView Current => _current;

        public bool MoveNext()
        {
            if (_position >= end) return false;
            RlpReader r = new(rlp.Span) { Position = _position };
            int contentLength = r.ReadSequenceLength();
            int contentEnd = r.Position + contentLength;
            ValueHash256 codeHash = r.DecodeValueKeccak() ?? throw new RlpException("CodeDiff.CodeHash must not be null");
            ReadOnlySpan<byte> code = r.DecodeByteArraySpan();
            _current = new CodeView(codeHash, code);
            _position = contentEnd;
            return true;
        }
    }

    /// <summary>Contract code captured when its hash first appears in a block, so replay can re-insert it.</summary>
    public readonly ref struct CodeView(ValueHash256 codeHash, ReadOnlySpan<byte> code)
    {
        public ValueHash256 CodeHash { get; } = codeHash;
        public ReadOnlySpan<byte> Code { get; } = code;
    }
}
