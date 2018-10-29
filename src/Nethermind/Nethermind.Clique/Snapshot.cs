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
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Blockchain;
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
        public LruCache<Keccak, Address> Sigcache;
        public UInt256 Number;
        public byte[] Hash;
        public HashSet<Address> Signers;
        public Dictionary<UInt64, Address> Recents;
        public List<Vote> Votes;
        public Dictionary<Address, Tally> Tally;

        Snapshot()
        {
            this.Votes = new List<Vote>();
        }

        public Snapshot(CliqueConfig config, LruCache<Keccak, Address> sigcache, UInt256 number, byte[] hash, HashSet<Address> signers, Dictionary<UInt64, Address> recents, Dictionary<Address, Tally> tally)
        {
            this.Config = config;
            this.Sigcache = sigcache;
            this.Number = number;
            this.Hash = hash;
            this.Signers = signers;
            this.Recents = recents;
            this.Votes = new List<Vote>();
            this.Tally = tally;
        }

        Snapshot Copy()
        {
            Snapshot s = new Snapshot();
            s.Config = Config;
            s.Sigcache = Sigcache;
            s.Number = Number;
            s.Hash = Hash;
            s.Signers = new HashSet<Address>();
            s.Recents = new Dictionary<ulong, Address>();
            s.Votes = new List<Vote>();
            s.Tally = new Dictionary<Address, Tally>();

            foreach (Address signer in Signers)
            {
                s.Signers.Add(signer);
            }
            foreach (var pair in Recents)
            {
                s.Recents[pair.Key] = pair.Value;
            }
            foreach (Vote vote in Votes)
            {
                s.Votes.Add(vote);
            }
            foreach (var pair in Tally)
            {
                s.Tally[pair.Key] = pair.Value;
            }
            return s;
        }

        public static Snapshot NewSnapshot(CliqueConfig config, LruCache<Keccak, Address> sigcache, UInt256 number, byte[] hash, Address[] signers)
        {
            HashSet<Address> signerSet = new HashSet<Address>();
            Dictionary<UInt64, Address> signerDict = new Dictionary<UInt64, Address>();
            Dictionary<Address, Tally> tally = new Dictionary<Address, Tally>();
            Snapshot snap = new Snapshot(config, sigcache, number, hash, signerSet, signerDict, tally);

            foreach (Address signer in signers)
            {
                snap.Signers.Add(signer);
            }
            return snap;
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
            Snapshot snap = serializer.Deserialize<Snapshot>(json);
            snap.Config = config;
            snap.Sigcache = sigcache;
            return snap;
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
            Snapshot snap = Copy();
            foreach (BlockHeader header in headers)
            {
                // Remove any votes on checkpoint blocks
                ulong number = (ulong)header.Number;
                if (number % Config.Epoch == 0)
                {
                    snap.Votes.Clear();
                    snap.Tally = new Dictionary<Address, Tally>();
                }
                // Delete the oldest signer from the recent list to allow it signing again
                {
                    ulong limit = (ulong)(snap.Signers.Count) / 2 + 1;
                    if (number >= limit)
                    {
                        snap.Recents.Remove((uint)number - limit);
                    }
                }
                // Resolve the authorization key and check against signers
                Address signer = header.GetBlockSealer(Sigcache);
                if (!snap.Signers.Contains(signer))
                {
                    throw new InvalidOperationException("Unauthorized signer");
                }
                foreach (Address recent in snap.Recents.Values)
                {
                    if (recent == signer)
                    {
                        throw new InvalidOperationException("Recently signed");
                    }
                }
                snap.Recents[(uint)number] = signer;
                // Header authorized, discard any previous votes from the signer
                for (int i = 0; i < snap.Votes.Count; i++)
                {
                    Vote vote = snap.Votes[i];
                    if (vote.Signer == signer && vote.Address == header.Beneficiary)
                    {
                        // Uncast the vote from the cached tally
                        snap.Uncast(vote.Address, vote.Authorize);
                        // Uncast the vote from the chronological list
                        // snap.Votes = append(snap.Votes[:i], snap.Votes[i+1:]...)
                        // TODO
                        break;
                    }
                }
                // Tally up the new vote from the signer
                bool authorize = header.Nonce == Nethermind.Clique.Clique.NonceAuthVote;
                if (snap.Cast(header.Beneficiary, authorize))
                {
                    Vote vote = new Vote(signer, number, header.Beneficiary, authorize);
                    snap.Votes.Add(vote);
                }
                // If the vote passed, update the list of signers
                Tally tally = snap.Tally[header.Beneficiary];
                if (tally.Votes > snap.Signers.Count / 2)
                {
                    if (tally.Authorize)
                    {
                        snap.Signers.Add(header.Beneficiary);
                    }
                    else
                    {
                        snap.Signers.Remove(header.Beneficiary);
                    }
                    // Signer list shrunk, delete any leftover recent caches
                    ulong limit = (ulong)snap.Signers.Count / 2 + 1;
                    if (number >= limit)
                    {
                        snap.Recents.Remove((uint)number - limit);
                    }
                    // Discard any previous votes the deauthorized signer cast
                    for (int i = 0; i < snap.Votes.Count; i++)
                    {
                        if (snap.Votes[i].Signer == header.Beneficiary)
                        {
                            // Uncast the vote from the cached tally
                            snap.Uncast(snap.Votes[i].Address, snap.Votes[i].Authorize);

                            // Uncast the vote from the chronological list
                            // snap.Votes = append(snap.Votes[:i], snap.Votes[i+1:]...)
                            // TODO
                            i--;
                        }
                    }
                    // Discard any previous votes around the just changed account
                    for (int i = 0; i < snap.Votes.Count; i++)
                    {
                        if (snap.Votes[i].Address == header.Beneficiary)
                        {
                            //snap.Votes = append(snap.Votes[:i], snap.Votes[i+1:]...)
                            // TODO
                            i--;
                        }
                    }
                    snap.Tally.Remove(header.Beneficiary);
                }
            }
            snap.Number += (uint)headers.Count;
            snap.Hash = BlockHeader.CalculateHash(headers[headers.Count - 1]).Bytes;
            return snap;
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