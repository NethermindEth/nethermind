// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;

namespace Nethermind.State.SnapServer;

/// <summary>Collects account leaves as the encoded <c>[path, slim account]</c> entries of an account range response.</summary>
/// <param name="byteLimit">The byte limit of the response, used to size the first buffer.</param>
public class AccountCollector(long byteLimit = 0) : RangeQueryVisitor.ILeafValueCollector
{
    // A full range of typical accounts fits in 2 MiB. A range of only minimal accounts at the hard limit needs more,
    // and the shared pool would round that up to 4 MiB for every response; Add grows the buffer in that rare case.
    private const int MaxInitialCapacity = 2 * 1024 * 1024;

    // An entry encodes to at most 3 bytes more than the 37 or more bytes that Collect counts for it, and the
    // visitor stops only after the entry that reaches the limit.
    public EncodedSnapEntries<PathWithAccount> Accounts { get; } =
        new(DecodeEntry, (int)Math.Min(Math.Min(byteLimit, ISnapStateServer.HardResponseByteLimit) * 9 / 8 + 256, MaxInitialCapacity));

    /// <remarks>
    /// The slim account is encoded here from <see cref="AccountStruct"/>, so it must stay byte-identical with the
    /// encoding of <see cref="AccountDecoder.Slim"/>. Tests check this.
    /// </remarks>
    /// <exception cref="TrieException">The leaf value is not an account.</exception>
    public int Collect(in ValueHash256 path, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || !AccountDecoder.Instance.TryDecodeStruct<OffFlag>(value, out AccountStruct account))
        {
            throw new TrieException("An account leaf must contain a decodable account value.");
        }

        int contentLength = Rlp.LengthOf(account.Nonce)
            + Rlp.LengthOf(account.Balance)
            + (account.HasStorage ? Rlp.LengthOfKeccakRlp : 1)
            + (account.HasCode ? Rlp.LengthOfKeccakRlp : 1);
        int accountLength = Rlp.LengthOfSequence(contentLength);

        RlpWriter writer = RlpWriter.ContentWriter(Accounts.Add(Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp + accountLength)));
        RlpSequence entry = writer.StartSequence(Rlp.LengthOfKeccakRlp + accountLength);
        writer.Encode(path);
        RlpSequence slimAccount = writer.StartSequence(contentLength);
        writer.Encode(account.Nonce);
        writer.Encode(account.Balance);
        if (account.HasStorage) writer.Encode(account.StorageRoot);
        else writer.EncodeEmptyByteArray();
        if (account.HasCode) writer.Encode(account.CodeHash);
        else writer.EncodeEmptyByteArray();
        writer.EndSequence(slimAccount);
        writer.EndSequence(entry);

        return 32 + accountLength;
    }

    private static PathWithAccount DecodeEntry(scoped ref RlpReader reader)
    {
        RlpReader entry = reader.EnterSequence(out int length);
        ValueHash256 path = entry.DecodeValueKeccakNonNull();
        AccountDecoder.Slim.TryDecodeStruct<OffFlag>(ref entry, out AccountStruct account);
        reader.ExitSequence(entry, length);
        return new PathWithAccount(path, account);
    }
}
