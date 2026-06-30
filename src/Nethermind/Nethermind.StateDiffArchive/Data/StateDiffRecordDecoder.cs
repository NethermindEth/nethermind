// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StateDiffArchive.Data;

/// <summary>
/// RLP encoder/decoder for <see cref="StateDiffRecord"/>. Positional; the leading version byte allows
/// forward-compatible additions.
/// <code>
/// StateDiffRecord = [
///   Version     (byte),
///   BlockNumber (uint64),
///   StateRoot   (32B),
///   Accounts = [
///     [ Address (20B), Change (byte 0|1|2), Account (RLP account | 0xC0 when not Set),
///       StorageCleared (bool), Slots = [ [ Index (uint256), Value (bytes) ], ... ] ],
///     ...
///   ],
///   Codes = [ [ CodeHash (32B), Code (bytes) ], ... ]
/// ]
/// </code>
/// </summary>
public sealed class StateDiffRecordDecoder : RlpDecoder<StateDiffRecord>
{
    public static StateDiffRecordDecoder Instance { get; } = new();

    private static readonly AccountDecoder AccountRlp = AccountDecoder.Instance;

    public override void Encode<TWriter>(ref TWriter w, StateDiffRecord item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        w.StartSequence(GetContentLength(item));
        w.Encode(item.Version);
        w.Encode(item.BlockNumber);
        w.Encode(item.StateRoot);

        w.StartSequence(GetAccountsContentLength(item.Accounts));
        foreach (AccountDiff a in item.Accounts)
        {
            w.StartSequence(GetAccountDiffContentLength(a));
            w.Encode(a.Address);
            w.Encode((byte)a.Change);
            // Only Set carries an account body; the AccountDecoder's null encoding is not self-describing,
            // so None/Deleted rely on the Change byte alone.
            if (a.Change == AccountChangeKind.Set) AccountRlp.Encode(ref w, a.Account);
            w.Encode(a.StorageCleared);

            w.StartSequence(GetSlotsContentLength(a.Slots));
            foreach (SlotDiff s in a.Slots)
            {
                w.StartSequence(GetSlotContentLength(s));
                w.Encode(s.Index);
                w.Encode(s.Value);
            }
        }

        w.StartSequence(GetCodesContentLength(item.Codes));
        foreach (CodeDiff c in item.Codes)
        {
            w.StartSequence(GetCodeContentLength(c));
            w.Encode(c.CodeHash);
            w.Encode(c.Code);
        }
    }

    public override int GetLength(StateDiffRecord item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Rlp.LengthOfSequence(GetContentLength(item));

    protected override StateDiffRecord DecodeInternal(ref RlpReader r, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        r.ReadSequenceLength();
        byte version = r.DecodeByte();
        ulong blockNumber = r.DecodeULong();
        Hash256 stateRoot = r.DecodeKeccak() ?? throw new RlpException("StateDiffRecord.StateRoot must not be null");

        int accountsEnd = r.Position + r.ReadSequenceLength();
        List<AccountDiff> accounts = [];
        while (r.Position < accountsEnd)
        {
            r.ReadSequenceLength();
            Address address = r.DecodeAddress() ?? throw new RlpException("AccountDiff.Address must not be null");
            AccountChangeKind change = (AccountChangeKind)r.DecodeByte();
            Account? account = change == AccountChangeKind.Set ? AccountRlp.Decode(ref r) : null;
            bool storageCleared = r.DecodeBool();

            int slotsEnd = r.Position + r.ReadSequenceLength();
            List<SlotDiff> slots = [];
            while (r.Position < slotsEnd)
            {
                r.ReadSequenceLength();
                UInt256 index = r.DecodeUInt256();
                byte[] value = r.DecodeByteArray();
                slots.Add(new SlotDiff(index, value));
            }

            accounts.Add(new AccountDiff(address, change, change == AccountChangeKind.Set ? account : null, storageCleared, slots));
        }

        int codesEnd = r.Position + r.ReadSequenceLength();
        List<CodeDiff> codes = [];
        while (r.Position < codesEnd)
        {
            r.ReadSequenceLength();
            ValueHash256 codeHash = r.DecodeValueKeccak() ?? throw new RlpException("CodeDiff.CodeHash must not be null");
            byte[] code = r.DecodeByteArray();
            codes.Add(new CodeDiff(codeHash, code));
        }

        return new StateDiffRecord(version, blockNumber, stateRoot, accounts, codes);
    }

    private static int GetContentLength(StateDiffRecord item)
        => Rlp.LengthOf(item.Version)
           + Rlp.LengthOf(item.BlockNumber)
           + Rlp.LengthOf(item.StateRoot)
           + Rlp.LengthOfSequence(GetAccountsContentLength(item.Accounts))
           + Rlp.LengthOfSequence(GetCodesContentLength(item.Codes));

    private static int GetAccountsContentLength(IReadOnlyList<AccountDiff> accounts)
    {
        int total = 0;
        foreach (AccountDiff a in accounts) total += Rlp.LengthOfSequence(GetAccountDiffContentLength(a));
        return total;
    }

    private static int GetAccountDiffContentLength(AccountDiff a)
    {
        int length = Rlp.LengthOf(a.Address)
                     + Rlp.LengthOf((byte)a.Change)
                     + Rlp.LengthOf((byte)(a.StorageCleared ? 1 : 0))
                     + Rlp.LengthOfSequence(GetSlotsContentLength(a.Slots));
        if (a.Change == AccountChangeKind.Set) length += AccountRlp.GetLength(a.Account);
        return length;
    }

    private static int GetSlotsContentLength(IReadOnlyList<SlotDiff> slots)
    {
        int total = 0;
        foreach (SlotDiff s in slots) total += Rlp.LengthOfSequence(GetSlotContentLength(s));
        return total;
    }

    private static int GetSlotContentLength(in SlotDiff s) => Rlp.LengthOf(s.Index) + Rlp.LengthOf(s.Value);

    private static int GetCodesContentLength(IReadOnlyList<CodeDiff> codes)
    {
        int total = 0;
        foreach (CodeDiff c in codes) total += Rlp.LengthOfSequence(GetCodeContentLength(c));
        return total;
    }

    private static int GetCodeContentLength(in CodeDiff c) => Rlp.LengthOfKeccakRlp + Rlp.LengthOf(c.Code);
}
