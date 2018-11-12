/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Clique
{
    internal class Snapshot
    {
        internal CliqueConfig Config { get; private set; }
        internal LruCache<Keccak, Address> SigCache { get; private set; }
        internal UInt256 Number { get; private set; }
        internal Keccak Hash { get; private set; }
        internal SortedList<Address, UInt256> Signers { get; }
        internal List<Vote> Votes;
        internal Dictionary<Address, Tally> Tally { get; private set; }

        internal Snapshot(CliqueConfig config, LruCache<Keccak, Address> sigCache, UInt256 number, Keccak hash, SortedList<Address, UInt256> signers, Dictionary<Address, Tally> tally)
        {
            Config = config;
            SigCache = sigCache;
            Number = number;
            Hash = hash;
            Signers = new SortedList<Address, UInt256>(signers, CliqueAddressComparer.Instance);
            Votes = new List<Vote>();
            Tally = tally;
        }

        internal Snapshot(CliqueConfig config, LruCache<Keccak, Address> sigCache, UInt256 number, Keccak hash, SortedList<Address, UInt256> signers)
            : this(config, sigCache, number, hash, signers, new Dictionary<Address, Tally>())
        {
        }

        private Snapshot Clone()
        {
            Snapshot clone = new Snapshot(Config, SigCache, Number, Hash, new SortedList<Address, UInt256>(Signers, CliqueAddressComparer.Instance), new Dictionary<Address, Tally>(Tally));
            clone.Votes = new List<Vote>(Votes);
            return clone;
        }

        public static Snapshot LoadSnapshot(CliqueConfig config, LruCache<Keccak, Address> sigCache, IDb db, Keccak hash)
        {
            Keccak key = GetSnapshotKey(hash);
            byte[] blob = db.Get(key);
            if (blob == null)
            {
                return null;
            }

            SnapshotDecoder decoder = new SnapshotDecoder();
            Snapshot snapshot = decoder.Decode(blob.AsRlpContext());
            snapshot.Config = config;
            snapshot.SigCache = sigCache;
            return snapshot;
        }

        public void Store(IDb db)
        {
            SnapshotDecoder decoder = new SnapshotDecoder();
            Rlp rlp = decoder.Encode(this);
            byte[] blob = rlp.Bytes;
            Keccak key = GetSnapshotKey(Hash);
            db.Set(key, blob);
        }

        public Snapshot Apply(List<BlockHeader> headers)
        {
            // Allow passing in no headers for cleaner code
            if (headers.Count == 0)
            {
                return this;
            }

            // Sanity check that the headers can be applied
            for (int i = 0; i < headers.Count - 1; i++)
            {
                if (headers[i].Number != Number + (UInt256) i + 1)
                {
                    throw new InvalidOperationException("Invalid voting chain");
                }
            }

            // Iterate through the headers and create a new snapshot
            Snapshot snapshot = Clone();
            foreach (BlockHeader header in headers)
            {
                // Remove any votes on checkpoint blocks
                UInt256 number = header.Number;
                if ((ulong) number % Config.Epoch == 0)
                {
                    snapshot.Votes.Clear();
                    snapshot.Tally = new Dictionary<Address, Tally>();
                }

                // Resolve the authorization key and check against signers
                Address signer = header.Author;
                if (!snapshot.Signers.ContainsKey(signer))
                {
                    throw new InvalidOperationException("Unauthorized signer");
                }

                
                if (HasSignedRecently(number, signer))
                {
                    throw new InvalidOperationException("Recently signed");
                }
                
                snapshot.Signers[signer] = number;
                
                // Header authorized, discard any previous votes from the signer
                for (int i = 0; i < snapshot.Votes.Count; i++)
                {
                    Vote vote = snapshot.Votes[i];
                    if (vote.Signer == signer && vote.Address == header.Beneficiary)
                    {
                        // Uncast the vote from the cached tally
                        snapshot.Uncast(vote.Address, vote.Authorize);
                        // Uncast the vote from the chronological list
                        snapshot.Votes.RemoveAt(i);
                        break;
                    }
                }

                // Tally up the new vote from the signer
                bool authorize = header.Nonce == CliqueSealEngine.NonceAuthVote;
                if (snapshot.Cast(header.Beneficiary, authorize))
                {
                    Vote vote = new Vote(signer, number, header.Beneficiary, authorize);
                    snapshot.Votes.Add(vote);
                }

                // If the vote passed, update the list of signers
                Tally tally = snapshot.Tally[header.Beneficiary];
                if (tally.Votes > snapshot.Signers.Count / 2)
                {
                    if (tally.Authorize)
                    {
                        snapshot.Signers.Add(header.Beneficiary, 0);
                    }
                    else
                    {
                        snapshot.Signers.Remove(header.Beneficiary);
                    }

                    // Discard any previous votes the deauthorized signer cast
                    for (int i = 0; i < snapshot.Votes.Count; i++)
                    {
                        if (snapshot.Votes[i].Signer == header.Beneficiary)
                        {
                            // Uncast the vote from the cached tally
                            snapshot.Uncast(snapshot.Votes[i].Address, snapshot.Votes[i].Authorize);

                            // Uncast the vote from the chronological list
                            snapshot.Votes.RemoveAt(i);
                            i--;
                        }
                    }

                    // Discard any previous votes around the just changed account
                    for (int i = 0; i < snapshot.Votes.Count; i++)
                    {
                        if (snapshot.Votes[i].Address == header.Beneficiary)
                        {
                            snapshot.Votes.RemoveAt(i);
                            i--;
                        }
                    }

                    snapshot.Tally.Remove(header.Beneficiary);
                }
            }

            snapshot.Number += (ulong) headers.Count;
            snapshot.Hash = BlockHeader.CalculateHash(headers[headers.Count - 1]);
            return snapshot;
        }

        public bool ValidVote(Address address, bool authorize)
        {
            bool signer = Signers.ContainsKey(address);
            return (signer && !authorize) || (!signer && authorize);
        }

        public bool Cast(Address address, bool authorize)
        {
            if (!Tally.ContainsKey(address))
            {
                Tally[address] = new Tally(authorize);
            }

            // Ensure the vote is meaningful
            if (!ValidVote(address, authorize))
            {
                return false;
            }

            // Cast the vote into tally
            Tally[address].Votes++;
            return true;
        }

        public bool Uncast(Address address, bool authorize)
        {
            // If there's no tally, it's a dangling vote, just drop
            if (!Tally.ContainsKey(address))
            {
                return true;
            }

            Tally tally = Tally[address];
            // Ensure we only revert counted votes
            if (tally.Authorize != authorize)
            {
                return false;
            }

            // Otherwise revert the vote
            if (tally.Votes > 1)
            {
                tally.Votes--;
            }
            else
            {
                Tally.Remove(address);
            }

            return true;
        }

        public ulong SignerLimit => (ulong) Signers.Count / 2 + 1;
        
        public bool HasSignedRecently(UInt256 number, Address signer)
        {
            UInt256 signedAt = Signers[signer];
            if (signedAt.IsZero)
            {
                return false;
            }
            
            return number - signedAt < SignerLimit;
        }
        
        public bool InTurn(UInt256 number, Address signer)
        {
            return (long) number % Signers.Count == Signers.IndexOfKey(signer);
        }

        private static byte[] _snapshotBytes = Encoding.UTF8.GetBytes("snapshot-");

        private static Keccak GetSnapshotKey(Keccak blockHash)
        {
            byte[] hashBytes = blockHash.Bytes;
            byte[] keyBytes = new byte[hashBytes.Length];
            for (int i = 0; i < _snapshotBytes.Length; i++)
            {
                keyBytes[i] = (byte) (hashBytes[i] ^ _snapshotBytes[i]);
            }

            return new Keccak(keyBytes);
        }
    }
}