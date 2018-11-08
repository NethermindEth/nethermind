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
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Clique
{
    internal class Snapshot
    {
        public CliqueConfig Config;
        public LruCache<Keccak, Address> SigCache;
        public uint Number;
        public byte[] Hash;
        public HashSet<Address> Signers;
        public Dictionary<UInt64, Address> Recent;
        public List<Vote> Votes;
        public Dictionary<Address, Tally> Tally;

        internal Snapshot()
        {
            Votes = new List<Vote>();
        }

        internal Snapshot(CliqueConfig config, LruCache<Keccak, Address> sigCache, UInt256 number, byte[] hash, HashSet<Address> signers, Dictionary<UInt64, Address> recent, Dictionary<Address, Tally> tally)
        {
            Config = config;
            SigCache = sigCache;
            Number = (uint)number;
            Hash = hash;
            Signers = signers;
            Recent = recent;
            Votes = new List<Vote>();
            Tally = tally;
        }
        
        private Snapshot Clone()
        {
            Snapshot clone = new Snapshot();
            clone.Config = Config;
            clone.SigCache = SigCache;
            clone.Number = Number;
            clone.Hash = Hash;
            clone.Signers = new HashSet<Address>();
            clone.Recent = new Dictionary<ulong, Address>();
            clone.Votes = new List<Vote>();
            clone.Tally = new Dictionary<Address, Tally>();

            foreach (Address signer in Signers)
            {
                clone.Signers.Add(signer);
            }
            
            foreach (var pair in Recent)
            {
                clone.Recent[pair.Key] = pair.Value;
            }
            
            foreach (Vote vote in Votes)
            {
                clone.Votes.Add(vote);
            }
            
            foreach (var pair in Tally)
            {
                clone.Tally[pair.Key] = pair.Value;
            }
            
            return clone;
        }

        public static Snapshot NewSnapshot(CliqueConfig config, LruCache<Keccak, Address> sigcache, UInt256 number, byte[] hash, Address[] signers)
        {
            HashSet<Address> signerSet = new HashSet<Address>();
            Dictionary<UInt64, Address> signerDict = new Dictionary<UInt64, Address>();
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            Snapshot snapshot = new Snapshot(config, sigcache, number, hash, signerSet, signerDict, tally);

            foreach (Address signer in signers)
            {
                snapshot.Signers.Add(signer);
            }
            return snapshot;
        }

        public static Snapshot LoadSnapshot(CliqueConfig config, LruCache<Keccak, Address> sigcache, IDb db, byte[] hash)
        {
            Keccak key = GetSnapshotKey(hash);
            byte[] blob = db.Get(key);
            if (blob == null)
            {
                return null;
            }
            String json = Encoding.UTF8.GetString(blob);
            JsonSerializer serializer = new JsonSerializer(NullLogManager.Instance);
            Snapshot snapshot = serializer.Deserialize<Snapshot>(json);
            snapshot.Config = config;
            snapshot.SigCache = sigcache;
            return snapshot;
        }

        public void Store(IDb db)
        {
            JsonSerializer serializer = new JsonSerializer(NullLogManager.Instance);
            string json = serializer.Serialize(this);
            byte[] blob = Encoding.UTF8.GetBytes(json);
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
                if (headers[i + 1].Number != headers[i].Number + 1)
                {
                    throw new InvalidOperationException("Invalid voting chain");
                }
            }
            if (headers[0].Number != Number + 1)
            {
                throw new InvalidOperationException("Invalid voting chain");
            }
            // Iterate through the headers and create a new snapshot
            Snapshot snapshot = Clone();
            foreach (BlockHeader header in headers)
            {
                // Remove any votes on checkpoint blocks
                ulong number = (ulong)header.Number;
                if (number % Config.Epoch == 0)
                {
                    snapshot.Votes.Clear();
                    snapshot.Tally = new Dictionary<Address, Tally>();
                }
                // Delete the oldest signer from the recent list to allow it signing again
                {
                    ulong limit = (ulong)(snapshot.Signers.Count) / 2 + 1;
                    if (number >= limit)
                    {
                        snapshot.Recent.Remove((uint)number - limit);
                    }
                }
                // Resolve the authorization key and check against signers
                Address signer = header.GetBlockSealer(SigCache);
                if (!snapshot.Signers.Contains(signer))
                {
                    throw new InvalidOperationException("Unauthorized signer");
                }
                foreach (Address recent in snapshot.Recent.Values)
                {
                    if (recent == signer)
                    {
                        throw new InvalidOperationException("Recently signed");
                    }
                }
                snapshot.Recent[(uint)number] = signer;
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
                bool authorize = header.Nonce == Clique.NonceAuthVote;
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
                        snapshot.Signers.Add(header.Beneficiary);
                    }
                    else
                    {
                        snapshot.Signers.Remove(header.Beneficiary);
                    }
                    // Signer list shrunk, delete any leftover recent caches
                    ulong limit = (ulong)snapshot.Signers.Count / 2 + 1;
                    if (number >= limit)
                    {
                        snapshot.Recent.Remove((uint)number - limit);
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
            snapshot.Number += (uint)headers.Count;
            snapshot.Hash = BlockHeader.CalculateHash(headers[headers.Count - 1]).Bytes;
            return snapshot;
        }

        public bool ValidVote(Address address, bool authorize)
        {
            bool signer = Signers.Contains(address);
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

        public bool Inturn(UInt256 number, Address signer)
        {
            Address[] signers = GetSigners();
            int offset = 0;
            while (offset < Signers.Count && signers[offset] != signer)
            {
                offset++;
            }
            return ((long)number % signers.Length == offset);
        }

        public Address[] GetSigners()
        {
            Address[] sigs = new Address[Signers.Count];
            Signers.CopyTo(sigs);
            Array.Sort(sigs, (x, y) => {
                int n = x.Bytes.Length;
                for (int j = 0; j < n; j++)
                {
                    if (x.Bytes[j] < y.Bytes[j])
                    {
                        return -1;
                    }
                    if (x.Bytes[j] > y.Bytes[j])
                    {
                        return 1;
                    }
                }
                return 0;
            });
            return sigs;
        }

        private static Keccak GetSnapshotKey(byte[] blockHash)
        {
            byte[] snapshotBytes = Encoding.UTF8.GetBytes("snapshot-");
            byte[] keyBytes = new byte[blockHash.Length];
            Array.Copy(blockHash, keyBytes, blockHash.Length);
            for (int i = 0; i < snapshotBytes.Length; i++)
            {
                keyBytes[i] ^= snapshotBytes[i];
            }
            return new Keccak(keyBytes);
        }
    }
}