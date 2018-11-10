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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Clique
{
    public class CliqueBlockProducer : IBlockProducer
    {
        private Clique _clique;
        private CliqueConfig _config;
        private BlockTree _blockTree;
        private Address _address;
        private Dictionary<Address, Boolean> _proposals = new Dictionary<Address, bool>();

        public CliqueBlockProducer(Clique clique, CliqueConfig config, BlockTree blockTree, Address address)
        {
            _clique = clique;
            _config = config;
            _blockTree = blockTree;
            _address = address;
        }

        public void Start()
        {
        }

        public async Task StopAsync()
        {
            await Task.CompletedTask;
        }

        private void Prepare(BlockHeader header)
        {
            // If the block isn't a checkpoint, cast a random vote (good enough for now)
            UInt256 number = header.Number;
            // Assemble the voting snapshot to check which votes make sense
            Snapshot snapshot = _clique.MakeSnapshot(number - 1, header.ParentHash);
            if ((ulong)number % _config.Epoch != 0)
            {
                // Gather all the proposals that make sense voting on
                List<Address> addresses = new List<Address>();
                foreach (var proposal in _proposals)
                {
                    Address address = proposal.Key;
                    bool authorize = proposal.Value;
                    if (snapshot.ValidVote(address, authorize))
                    {
                        addresses.Append(address);
                    }
                }

                // If there's pending proposals, cast a vote on them
                if (addresses.Count > 0)
                {
                    Random rnd = new Random();
                    header.Beneficiary = addresses[rnd.Next(addresses.Count)];
                    if (_proposals[header.Beneficiary])
                    {
                        header.Nonce = Clique.NonceAuthVote;
                    }
                    else
                    {
                        header.Nonce = Clique.NonceDropVote;
                    }
                }
            }

            // Set the correct difficulty
            header.Difficulty = CalculateDifficulty(snapshot, _address);
            // Ensure the extra data has all it's components
            if (header.ExtraData.Length < Clique.ExtraVanity)
            {
                for (int i = 0; i < Clique.ExtraVanity - header.ExtraData.Length; i++)
                {
                    header.ExtraData.Append((byte)0);
                }
            }

            header.ExtraData = header.ExtraData.Take(Clique.ExtraVanity).ToArray();

            if ((ulong)number % _config.Epoch == 0)
            {
                foreach (Address signer in snapshot.Signers)
                {
                    foreach (byte addressByte in signer.Bytes)
                    {
                        header.ExtraData.Append(addressByte);
                    }
                }
            }

            byte[] extraSeal = new byte[Clique.ExtraSeal];
            for (int i = 0; i < Clique.ExtraSeal; i++)
            {
                header.ExtraData.Append((byte)0);
            }

            // Mix digest is reserved for now, set to empty
            // Ensure the timestamp has the correct delay
            BlockHeader parent = _blockTree.FindHeader(header.ParentHash);
            if (parent == null)
            {
                throw new InvalidOperationException("Unknown ancestor");
            }

            header.Timestamp = parent.Timestamp + _config.Period;
            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (header.Timestamp < currentTimestamp)
            {
                header.Timestamp = new UInt256(currentTimestamp);
            }
        }

        private Block Finalize(StateProvider state, BlockHeader header, Transaction[] txs, BlockHeader[] uncles, TransactionReceipt[] receipts)
        {
            // No block rewards in PoA, so the state remains as is and uncles are dropped
            header.StateRoot = state.StateRoot;
            header.OmmersHash = BlockHeader.CalculateHash((BlockHeader)null);
            // Assemble and return the final block for sealing
            return new Block(header, txs, null);
        }

        private UInt256 CalculateDifficulty(ulong time, BlockHeader parent)
        {
            UInt256 parentNumber = parent.Number;
            Snapshot snapshot = _clique.MakeSnapshot(parentNumber, BlockHeader.CalculateHash(parent));
            return CalculateDifficulty(snapshot, _address);
        }

        private UInt256 CalculateDifficulty(Snapshot snapshot, Address signer)
        {
            if (snapshot.Inturn(snapshot.Number + 1, signer))
            {
                return new UInt256(Clique.DiffInTurn);
            }

            return new UInt256(Clique.DiffNoTurn);
        }
    }
}