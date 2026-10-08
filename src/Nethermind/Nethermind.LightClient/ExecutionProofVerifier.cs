// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.LightClient;

internal static class ExecutionProofVerifier
{
    internal const int MaxProofNodes = 65;
    internal const int MaxNodeBytes = 1024;

    internal static Account VerifyAccount(Hash256 stateRoot, Address address, byte[][] proof)
    {
        byte[] value = ReadValue(stateRoot, Keccak.Compute(address.Bytes), proof);
        if (value.Length == 0) return Account.TotallyEmpty;

        RlpReader reader = new(value);
        Account account = AccountDecoder.Instance.Decode(ref reader)
            ?? throw new InvalidDataException("An account proof contains an invalid account.");
        if (!value.AsSpan().SequenceEqual(AccountDecoder.Instance.EncodeAsBytes(account)))
            throw new InvalidDataException("An account proof contains noncanonical account RLP.");
        return account;
    }

    internal static UInt256 VerifyStorage(Hash256 storageRoot, UInt256 slot, byte[][] proof)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        byte[] value = ReadValue(storageRoot, Keccak.Compute(slotBytes), proof);
        if (value.Length == 0) return UInt256.Zero;

        return DecodeStorageRlp(value);
    }

    internal static UInt256 DecodeStorageRlp(byte[] value)
    {

        RlpReader reader = new(value);
        UInt256 result = reader.DecodeUInt256();
        if (result.IsZero || !value.AsSpan().SequenceEqual(Rlp.Encode(result).Bytes))
            throw new InvalidDataException("A storage proof contains noncanonical storage RLP.");
        return result;
    }

    internal static void VerifyCode(Hash256 codeHash, byte[] code)
    {
        if (Keccak.Compute(code) != codeHash)
            throw new InvalidDataException("Contract code does not match its authenticated account hash.");
    }

    private static byte[] ReadValue(Hash256 root, Hash256 key, byte[][] proof)
    {
        if (proof.Length > MaxProofNodes)
            throw new InvalidDataException("An execution proof contains too many nodes.");

        using MemDb nodes = MemDb.WithCapacity(proof.Length);
        foreach (byte[] node in proof)
        {
            if (node is null || node.Length is 0 or > MaxNodeBytes)
                throw new InvalidDataException("An execution proof contains an invalid node size.");
            nodes[Keccak.Compute(node).Bytes] = node;
        }

        // Indexing by computed hashes prevents a provider from substituting a referenced child.
        RawScopedTrieStore store = new(new NodeStorage(nodes, INodeStorage.KeyScheme.Hash));
        PatriciaTree tree = new(store, root, allowCommits: false, NullLogManager.Instance);
        try
        {
            return tree.Get(key.Bytes).ToArray();
        }
        catch (TrieException exception)
        {
            throw new InvalidDataException("An execution proof is invalid or incomplete.", exception);
        }
    }
}
