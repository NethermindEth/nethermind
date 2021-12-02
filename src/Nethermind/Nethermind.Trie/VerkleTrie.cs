//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Mail;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Int256;

namespace Nethermind.Trie
{
    public class VerkleTrie
    {
        // public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;
        // public TrieType TrieType { get; protected set; }
        // private Keccak _rootHash = Keccak.EmptyTreeHash;
        // public Keccak RootHash
        // {
            // get => _rootHash;
            // set => SetRootHash(value, true);
        // }

        // public void Commit(long blockNumber)
        // {
        //     
        // }
        // public void UpdateRootHash()
        // {
        //     
        // }
        
        private readonly ILogger _logger;
        // public const int OneNodeAvgMemoryEstimate = 384;

        private readonly IntPtr _verkleTrieObj;
        
        public static readonly UInt256 EmptyTreeHash = UInt256.Zero;
        public TrieType TrieType { get; protected set; }
        
        private UInt256 _rootHash = UInt256.Zero;
        
        private readonly bool _allowCommits;
        
        public UInt256 RootHash
        {
            get => _rootHash;
            set => SetRootHash(value);
        }

        public VerkleTrie()
            : this(EmptyTreeHash, true, NullLogManager.Instance)
        {
        }
        public VerkleTrie(ILogManager? logManager)
            : this(EmptyTreeHash, true, logManager)
        {
        }

        public VerkleTrie(
            UInt256 rootHash,
            bool allowCommits,
            ILogManager? logManager)
        {
            // TODO: do i need to pass roothash here to rust to use for initialization of the library?
            _verkleTrieObj = RustVerkleLib.VerkleTrieNew();
            
            _logger = logManager?.GetClassLogger<VerkleTrie>() ?? throw new ArgumentNullException(nameof(logManager));
            _allowCommits = allowCommits;
            RootHash = rootHash;
        }


        [DebuggerStepThrough]
        // TODO: add functionality to start with a given root hash (traverse from a starting node)
        // public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
        public byte[]? Get(Span<byte> rawKey)
        {
            byte[] result = RustVerkleLib.VerkleTrieGet(_verkleTrieObj, rawKey.ToArray());
            return result;
        }
        
        
        [DebuggerStepThrough]
        public void Set(Span<byte> rawKey, byte[] value)
        {
            if (_logger.IsTrace)
                _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");
            // TODO; error handling here? or at least a way to check if the operation was successful
            RustVerkleLib.VerkleTrieInsert(_verkleTrieObj, rawKey.ToArray(), value);
        }
        
        private void SetRootHash(UInt256? value)
        {
            _rootHash = value ?? UInt256.Zero; // nulls were allowed before so for now we leave it this way
            // TODO: set RootRef here if needed by the client, else can be handled in rust itself
        }
        
        public void UpdateRootHash()
        {
            // TODO: add function to the rust_verkle_wrapper to get root hash to be updated here
            SetRootHash(EmptyTreeHash);
        }
        
        // This function is impl in the rust verkle trie
        // private void Commit(NodeCommitInfo nodeCommitInfo);

    }
}
