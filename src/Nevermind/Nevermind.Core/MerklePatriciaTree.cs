using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class StateTrie
    {
        public StateTrie(Dictionary<Address, Account> data)
        {
            Dictionary<Keccak, Rlp> dictionary = data.ToDictionary(p => Keccak.Compute(p.Key.Bytes), p => Rlp.Encode(p.Value));
        }
    }

    public class StorageTrie
    {
        public StorageTrie(Dictionary<BigInteger, BigInteger> data)
        {
            Dictionary<Keccak, Rlp> dictionary = data.ToDictionary(p => Keccak.Compute(Rlp.Encode(p.Key)), p => Rlp.Encode(p.Value));
        }
    }

    public class ReceiptTrie
    {
        public ReceiptTrie(Dictionary<BigInteger, TransactionReceipt> data)
        {
            Dictionary<Rlp, Rlp> dictionary = data.ToDictionary(p => Rlp.Encode(p.Key), p => Rlp.Encode(p.Value));
        }
    }

    public class TransactionTrie
    {
        public TransactionTrie(Dictionary<BigInteger, Transaction> data)
        {
            Dictionary<Rlp, Rlp> dictionary = data.ToDictionary(p => Rlp.Encode(p.Key), p => Rlp.Encode(p.Value));
        }
    }

    //public abstract class PatriciaNode
    //{
    //}

    //public class NullNode : PatriciaNode
    //{
    //}

    //public class BranchNode : PatriciaNode
    //{
    //}

    //public class LeafNode : PatriciaNode
    //{
    //}

    //public class ExtensionNode : PatriciaNode
    //{
    //}

    public class TrieDb
    {
        public Dictionary<Keccak, Rlp> Data { get; set; }
    }

    public class MerklePatriciaTree
    {
        private TrieDb _db = new TrieDb();

        private static byte[] GetNodeCap(int index, StateDatabase stateDatabase)
        {
            if (!stateDatabase.State.Any())
            {
                return new byte[] { };
            }

            byte[] node = ComposeNode(index, stateDatabase);
            if (node.Length < 32)
            {
                return node;
            }

            return Keccak.Compute(node).Bytes;
        }

        public MerklePatriciaTree(StateDatabase stateDatabase)
        {
            if (!stateDatabase.State.Any())
            {
                _nodes[0] = new byte[] { };
            }

            for (int i = 0; i < stateDatabase.State.Count; i++)
            {
                _nodes[i] = GetNodeCap(i, stateDatabase);
                i++;
            }
        }

        private readonly Dictionary<int, byte[]> _nodes = new Dictionary<int, byte[]>();

        public byte[] Root => _nodes[0];

        public static byte[] ComposeNode(int index, StateDatabase stateDatabase)
        {
            throw new NotImplementedException();
        }
    }
}
