// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
//using Nethermind.Core2.Containers;
//using Nethermind.Core2.Crypto;
//using Nethermind.Core2.Types;
using Nethermind.Int256;
using Nethermind.Merkleization;
using NUnit.Framework;
using Shouldly;

namespace Nethermind.Serialization.Ssz.Test
{
    [TestFixture]
    public class HashTreeRootTests
    {
        [SetUp]
        public void Setup()
        {
            Ssz.Init(
                32,
                4,
                2048,
                32,
                1024,
                8192,
                65536,
                8192,
                16_777_216,
                1_099_511_627_776,
                16,
                1,
                128,
                16,
                16
            );
        }

        //[Test]
        //public void Can_merkleize_epoch_0()
        //{
        //    // arrange
        //    Epoch epoch = Epoch.Zero;

        //    // act
        //    Merkleizer merklezier = new Merkleizer(0);
        //    merklezier.Feed(epoch);
        //    UInt256 root = merklezier.CalculateRoot();
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Chunk(new byte[] { 0x0 }).ToArray();
        //    bytes.ToArray().ShouldBe(expected);
        //}

        //[Test]
        //public void Can_merkleize_epoch_1()
        //{
        //    // arrange
        //    Epoch epoch = Epoch.One;

        //    // act
        //    Merkleizer merklezier = new Merkleizer(0);
        //    merklezier.Feed(epoch);
        //    UInt256 root = merklezier.CalculateRoot();
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Chunk(new byte[] { 0x1 }).ToArray();
        //    bytes.ToArray().ShouldBe(expected);
        //}

        [Test]
        public void Can_merkleize_bytes32()
        {
            // arrange
            Bytes32 bytes32 = new Bytes32(Enumerable.Repeat((byte)0x34, 32).ToArray());

            // act
            Merkle.Ize(out UInt256 root, bytes32);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

            // assert
            byte[] expected = Enumerable.Repeat((byte)0x34, 32).ToArray();
            bytes.ToArray().ShouldBe(expected);
        }

        //[Test]
        //public void Can_merkleize_checkpoint()
        //{
        //    // arrange
        //    Checkpoint checkpoint = new Checkpoint(
        //        new Epoch(3),
        //        new Root(Enumerable.Repeat((byte)0x34, 32).ToArray())
        //    );

        //    // act
        //    Merkle.Ize(out UInt256 root, checkpoint);
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Hash(
        //        HashUtility.Chunk(new byte[] { 0x03 }),
        //        Enumerable.Repeat((byte)0x34, 32).ToArray()
        //    ).ToArray();
        //    bytes.ToArray().ShouldBe(expected);
        //}

        //[Test]
        //public void Can_merkleize_attestion_data()
        //{
        //    // arrange
        //    AttestationData attestationData = new AttestationData(
        //        Slot.One,
        //        new CommitteeIndex(2),
        //        new Root(Enumerable.Repeat((byte)0x12, 32).ToArray()),
        //        new Checkpoint(
        //            new Epoch(3),
        //            new Root(Enumerable.Repeat((byte)0x34, 32).ToArray())
        //        ),
        //        new Checkpoint(
        //            new Epoch(4),
        //            new Root(Enumerable.Repeat((byte)0x56, 32).ToArray())
        //        )
        //    );

        //    // act
        //    Merkleizer merklezier = new Merkleizer(0);
        //    merklezier.Feed(attestationData);
        //    UInt256 root = merklezier.CalculateRoot();
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Hash(
        //        HashUtility.Hash(
        //            HashUtility.Hash(
        //                HashUtility.Chunk(new byte[] { 0x01 }), // slot
        //                HashUtility.Chunk(new byte[] { 0x02 }) // committee
        //            ),
        //            HashUtility.Hash(
        //                Enumerable.Repeat((byte)0x12, 32).ToArray(), // beacon block root
        //                HashUtility.Hash( // source
        //                    HashUtility.Chunk(new byte[] { 0x03 }),
        //                    Enumerable.Repeat((byte)0x34, 32).ToArray()
        //                )
        //            )
        //        ),
        //        HashUtility.Merge(
        //            HashUtility.Hash( // target
        //                HashUtility.Chunk(new byte[] { 0x04 }),
        //                Enumerable.Repeat((byte)0x56, 32).ToArray()
        //            ),
        //            HashUtility.ZeroHashes(0, 2)
        //        )
        //    ).ToArray();
        //    bytes.ToArray().ShouldBe(expected);
        //}

        //[Test]
        //public void Can_merkleize_deposit_data()
        //{
        //    // arrange
        //    DepositData depositData = new DepositData(
        //        new BlsPublicKey(Enumerable.Repeat((byte)0x12, BlsPublicKey.Length).ToArray()),
        //        new Bytes32(Enumerable.Repeat((byte)0x34, Bytes32.Length).ToArray()),
        //        new Gwei(5),
        //        new BlsSignature(Enumerable.Repeat((byte)0x67, BlsSignature.Length).ToArray())
        //    );

        //    // act
        //    Merkle.Ize(out UInt256 root, depositData);
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Hash(
        //        HashUtility.Hash(
        //            HashUtility.Hash( // public key
        //                Enumerable.Repeat((byte)0x12, 32).ToArray(),
        //                HashUtility.Chunk(Enumerable.Repeat((byte)0x12, 16).ToArray())
        //            ),
        //            Enumerable.Repeat((byte)0x34, Bytes32.Length).ToArray() // withdrawal credentials
        //        ),
        //        HashUtility.Hash(
        //            HashUtility.Chunk(new byte[] { 0x05 }), // amount
        //            HashUtility.Hash( // signature
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray()
        //                ),
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0x00, 32).ToArray()
        //                )
        //            )
        //        )
        //    );

        //    TestContext.WriteLine("root: {0:x}", root);
        //    TestContext.WriteLine("bytes: {0}", bytes.ToHexString(true));
        //    TestContext.WriteLine("expected: {0}", expected.ToHexString(true));

        //    bytes.ToArray().ShouldBe(expected);
        //}

        //[Test]
        //public void Can_merkleize_deposit_data_list()
        //{
        //    // arrange
        //    DepositData depositData1 = new DepositData(
        //        new BlsPublicKey(Enumerable.Repeat((byte)0x12, BlsPublicKey.Length).ToArray()),
        //        new Bytes32(Enumerable.Repeat((byte)0x34, Bytes32.Length).ToArray()),
        //        new Gwei(5),
        //        new BlsSignature(Enumerable.Repeat((byte)0x67, BlsSignature.Length).ToArray())
        //    );
        //    DepositData depositData2 = new DepositData(
        //        new BlsPublicKey(Enumerable.Repeat((byte)0x9a, BlsPublicKey.Length).ToArray()),
        //        new Bytes32(Enumerable.Repeat((byte)0xbc, Bytes32.Length).ToArray()),
        //        new Gwei(0xd),
        //        new BlsSignature(Enumerable.Repeat((byte)0xef, BlsSignature.Length).ToArray())
        //    );
        //    List<DepositData> depositDataList = new List<DepositData> { depositData1, depositData2 };

        //    // act
        //    Merkle.Ize(out UInt256 root, depositDataList);
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    Merkle.Ize(out UInt256 root0, depositDataList[0]);
        //    TestContext.WriteLine("root0: {0:x}", root0);
        //    Merkle.Ize(out UInt256 root1, depositDataList[1]);
        //    TestContext.WriteLine("root1: {0:x}", root1);

        //    // assert
        //    byte[] hash1 = HashUtility.Hash(
        //        HashUtility.Hash(
        //            HashUtility.Hash( // public key
        //                Enumerable.Repeat((byte)0x12, 32).ToArray(),
        //                HashUtility.Chunk(Enumerable.Repeat((byte)0x12, 16).ToArray())
        //            ),
        //            Enumerable.Repeat((byte)0x34, Bytes32.Length).ToArray() // withdrawal credentials
        //        ),
        //        HashUtility.Hash(
        //            HashUtility.Chunk(new byte[] { 0x05 }), // amount
        //            HashUtility.Hash( // signature
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray()
        //                ),
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0x00, 32).ToArray()
        //                )
        //            )
        //        )
        //    );
        //    byte[] hash2 = HashUtility.Hash(
        //        HashUtility.Hash(
        //            HashUtility.Hash( // public key
        //                Enumerable.Repeat((byte)0x9a, 32).ToArray(),
        //                HashUtility.Chunk(Enumerable.Repeat((byte)0x9a, 16).ToArray())
        //            ),
        //            Enumerable.Repeat((byte)0xbc, Bytes32.Length).ToArray() // withdrawal credentials
        //        ),
        //        HashUtility.Hash(
        //            HashUtility.Chunk(new byte[] { 0x0d }), // amount
        //            HashUtility.Hash( // signature
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0xef, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0xef, 32).ToArray()
        //                ),
        //                HashUtility.Hash(
        //                    Enumerable.Repeat((byte)0xef, 32).ToArray(),
        //                    Enumerable.Repeat((byte)0x00, 32).ToArray()
        //                )
        //            )
        //        )
        //    );

        //    TestContext.WriteLine("Hash1: {0}", Bytes.ToHexString(hash1, true));
        //    TestContext.WriteLine("Hash2: {0}", Bytes.ToHexString(hash2, true));

        //    byte[] hashList = HashUtility.Merge( // list, depth 32
        //        HashUtility.Hash(
        //            hash1,
        //            hash2
        //        ),
        //        HashUtility.ZeroHashes(1, 32)
        //    ).ToArray();
        //    TestContext.WriteLine("Hash list: {0}", Bytes.ToHexString(hashList, true));

        //    byte[] expected = HashUtility.Hash(
        //        hashList,
        //        HashUtility.Chunk(new byte[] { 0x02 }) // mix in length
        //    );
        //    TestContext.WriteLine("Hash expected: {0}", Bytes.ToHexString(expected, true));

        //    bytes.ToArray().ShouldBe(expected);
        //}

        // TODO: Add tests for deposit, and deposit list (for beacon block body)

        //        [Test]
        //        public void Can_merkleize_deposit()
        //        {
        //            // arrange
        //            Deposit deposit = new Deposit(
        //                Enumerable.Repeat(new Hash32(), 33),
        //                new DepositData(
        //                    new BlsPublicKey(Enumerable.Repeat((byte) 0x12, BlsPublicKey.Length).ToArray()),
        //                    new Hash32(Enumerable.Repeat((byte) 0x34, Hash32.Length).ToArray()),
        //                    new Gwei(5),
        //                    new BlsSignature(Enumerable.Repeat((byte) 0x67, BlsSignature.Length).ToArray())
        //                )
        //            );
        //
        //            // act
        //            Merkle.Ize(out UInt256 root, deposit);
        //            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
        //
        //            // assert
        //            byte[] expected = HashUtility.Hash(
        //                HashUtility.Hash(
        //                    HashUtility.Hash( // public key
        //                        Enumerable.Repeat((byte) 0x12, 32).ToArray(),
        //                        HashUtility.Chunk(Enumerable.Repeat((byte) 0x12, 16).ToArray())
        //                    ),
        //                    Enumerable.Repeat((byte) 0x34, Hash32.Length).ToArray() // withdrawal credentials
        //                ),
        //                HashUtility.Hash(
        //                    HashUtility.Chunk(new byte[] {0x05}), // amount
        //                    HashUtility.Hash( // signature
        //                        HashUtility.Hash(
        //                            Enumerable.Repeat((byte) 0x67, 32).ToArray(),
        //                            Enumerable.Repeat((byte) 0x67, 32).ToArray()
        //                        ),
        //                        HashUtility.Hash(
        //                            Enumerable.Repeat((byte) 0x67, 32).ToArray(),
        //                            Enumerable.Repeat((byte) 0x00, 32).ToArray()
        //                        )
        //                    )
        //                )
        //            );
        //            
        //            TestContext.WriteLine("root: {0:x}", root);
        //            TestContext.WriteLine("bytes: {0}", bytes.ToHexString(true));
        //            TestContext.WriteLine("expected: {0}", expected.ToHexString(true));
        //
        //            bytes.ToArray().ShouldBe(expected);
        //        }

        //[Test]
        //public void Can_merkleize_eth1data()
        //{
        //    // arrange
        //    Eth1Data eth1Data = new Eth1Data(
        //        new Root(Enumerable.Repeat((byte)0x34, Root.Length).ToArray()),
        //        5,
        //        new Bytes32(Enumerable.Repeat((byte)0x67, Bytes32.Length).ToArray())
        //    );

        //    // act
        //    Merkle.Ize(out UInt256 root, eth1Data);
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] expected = HashUtility.Hash(
        //        HashUtility.Hash(
        //            Enumerable.Repeat((byte)0x34, 32).ToArray(),
        //            HashUtility.Chunk(new byte[] { 0x05 })
        //        ),
        //        HashUtility.Hash(
        //            Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //            Enumerable.Repeat((byte)0x00, 32).ToArray()
        //        )
        //    ).ToArray();
        //    bytes.ToArray().ShouldBe(expected);
        //}

        //[Test]
        //public void Can_merkleize_empty_beacon_block_body()
        //{
        //    // arrange
        //    List<ProposerSlashing> proposerSlashings = new List<ProposerSlashing>();
        //    List<AttesterSlashing> attesterSlashings = new List<AttesterSlashing>();
        //    List<Attestation> attestations = new List<Attestation>();
        //    List<Deposit> deposits = new List<Deposit>();
        //    List<SignedVoluntaryExit> voluntaryExits = new List<SignedVoluntaryExit>();

        //    BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
        //        new BlsSignature(Enumerable.Repeat((byte)0x12, BlsSignature.Length).ToArray()),
        //        new Eth1Data(
        //            new Root(Enumerable.Repeat((byte)0x34, Root.Length).ToArray()),
        //            5,
        //            new Bytes32(Enumerable.Repeat((byte)0x67, Bytes32.Length).ToArray())
        //        ),
        //        new Bytes32(Enumerable.Repeat((byte)0x89, Bytes32.Length).ToArray()),
        //        proposerSlashings,
        //        attesterSlashings,
        //        attestations,
        //        deposits,
        //        voluntaryExits
        //    );

        //    // act
        //    Merkle.Ize(out UInt256 root, beaconBlockBody);
        //    Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));

        //    // assert
        //    byte[] proposerSlashingsHash = HashUtility.Hash(
        //            HashUtility.ZeroHashes(4, 5)[0],
        //            HashUtility.Chunk(new byte[] { 0x00 })
        //        );
        //    byte[] attesterSlashingsHash = HashUtility.Hash(
        //        HashUtility.ZeroHashes(0, 1)[0],
        //        HashUtility.Chunk(new byte[] { 0x00 })
        //    );
        //    byte[] attestationsHash = HashUtility.Hash(
        //        HashUtility.ZeroHashes(7, 8)[0],
        //        HashUtility.Chunk(new byte[] { 0x00 })
        //    );
        //    byte[] depositsHash = HashUtility.Hash(
        //        HashUtility.ZeroHashes(4, 5)[0],
        //        HashUtility.Chunk(new byte[] { 0x00 })
        //    );
        //    byte[] voluntaryExitsHash = HashUtility.Hash(
        //        HashUtility.ZeroHashes(4, 5)[0],
        //        HashUtility.Chunk(new byte[] { 0x00 })
        //    );

        //    byte[] expected = HashUtility.Hash(
        //        HashUtility.Hash(
        //            HashUtility.Hash(
        //                HashUtility.Hash( // randao
        //                    HashUtility.Hash(
        //                        Enumerable.Repeat((byte)0x12, 32).ToArray(),
        //                        Enumerable.Repeat((byte)0x12, 32).ToArray()
        //                    ),
        //                    HashUtility.Hash(
        //                        Enumerable.Repeat((byte)0x12, 32).ToArray(),
        //                        Enumerable.Repeat((byte)0x00, 32).ToArray()
        //                    )
        //                ),
        //                HashUtility.Hash( // eth1data
        //                    HashUtility.Hash(
        //                        Enumerable.Repeat((byte)0x34, 32).ToArray(),
        //                        HashUtility.Chunk(new byte[] { 0x05 })
        //                    ),
        //                    HashUtility.Hash(
        //                        Enumerable.Repeat((byte)0x67, 32).ToArray(),
        //                        Enumerable.Repeat((byte)0x00, 32).ToArray()
        //                    )
        //                )
        //            ),
        //            HashUtility.Hash(
        //                Enumerable.Repeat((byte)0x89, Bytes32.Length).ToArray(), // graffiti
        //                proposerSlashingsHash // proposer slashings
        //            )
        //        ),
        //        HashUtility.Hash(
        //            HashUtility.Hash(
        //                attesterSlashingsHash, // attester slashings
        //                attestationsHash // attestations
        //            ),
        //            HashUtility.Hash(
        //                depositsHash, // deposits
        //                voluntaryExitsHash // voluntary exits
        //            )
        //        )
        //    );

        //    bytes.ToArray().ShouldBe(expected);
        //}

        // TODO: Finish test for beacon block body with data, e.g. deposit, to get it working

        //        [Test]
        //        public void Can_merkleize_beacon_block_body_with_deposit()
        //        {
        //            // arrange
        //            List<ProposerSlashing> proposerSlashings = new List<ProposerSlashing>();
        //            List<AttesterSlashing> attesterSlashings = new List<AttesterSlashing>();
        //            List<Attestation> attestations = new List<Attestation>();
        //            List<Deposit> deposits = new List<Deposit>()
        //            {
        //                new Deposit(
        //                    Enumerable.Repeat(new Hash32(), 33),
        //                    new DepositData(
        //                        new BlsPublicKey(Enumerable.Repeat((byte) 0x12, BlsPublicKey.Length).ToArray()),
        //                        new Hash32(Enumerable.Repeat((byte) 0x34, Hash32.Length).ToArray()),
        //                        new Gwei(5),
        //                        new BlsSignature(Enumerable.Repeat((byte) 0x67, BlsSignature.Length).ToArray())
        //                    )
        //                )
        //            };
        //            List<VoluntaryExit> voluntaryExits = new List<VoluntaryExit>();
        //            
        //            BeaconBlockBody beaconBlockBody = new BeaconBlockBody(
        //                new BlsSignature(Enumerable.Repeat((byte) 0x12, BlsSignature.Length).ToArray()),
        //                new Eth1Data(
        //                    new Hash32(Enumerable.Repeat((byte) 0x34, Hash32.Length).ToArray()),
        //                    5,
        //                    new Hash32(Enumerable.Repeat((byte) 0x67, Hash32.Length).ToArray())
        //                ),
        //                new Bytes32(Enumerable.Repeat((byte) 0x89, Bytes32.Length).ToArray()),
        //                proposerSlashings,
        //                attesterSlashings,
        //                attestations,
        //                deposits,
        //                voluntaryExits
        //            );
        //
        //            // act
        //            Merkle.Ize(out UInt256 root, beaconBlockBody);
        //            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
        //
        //            // assert
        //            byte[] proposerSlashingsHash = HashUtility.Hash(
        //                    HashUtility.ZeroHashes(4, 5)[0],
        //                    HashUtility.Chunk(new byte[] { 0x00 })
        //                );
        //            byte[] attesterSlashingsHash = HashUtility.Hash(
        //                HashUtility.ZeroHashes(0, 1)[0],
        //                HashUtility.Chunk(new byte[] { 0x00 })
        //            );
        //            byte[] attestationsHash = HashUtility.Hash(
        //                HashUtility.ZeroHashes(7, 8)[0],
        //                HashUtility.Chunk(new byte[] { 0x00 })
        //            );
        //            byte[] depositsHash = HashUtility.Hash(
        //                HashUtility.ZeroHashes(4, 5)[0],
        //                HashUtility.Chunk(new byte[] { 0x00 })
        //            );
        //            byte[] voluntaryExitsHash = HashUtility.Hash(
        //                HashUtility.ZeroHashes(4, 5)[0],
        //                HashUtility.Chunk(new byte[] { 0x00 })
        //            );
        //            
        //            byte[] expected = HashUtility.Hash(
        //                HashUtility.Hash(
        //                    HashUtility.Hash(
        //                        HashUtility.Hash( // randao
        //                            HashUtility.Hash(
        //                                Enumerable.Repeat((byte) 0x12, 32).ToArray(),
        //                                Enumerable.Repeat((byte) 0x12, 32).ToArray()
        //                            ),
        //                            HashUtility.Hash(
        //                                Enumerable.Repeat((byte) 0x12, 32).ToArray(),
        //                                Enumerable.Repeat((byte) 0x00, 32).ToArray()
        //                            )
        //                        ),
        //                        HashUtility.Hash( // eth1data
        //                            HashUtility.Hash(
        //                                Enumerable.Repeat((byte) 0x34, 32).ToArray(),
        //                                HashUtility.Chunk(new byte[] {0x05})
        //                            ),
        //                            HashUtility.Hash(
        //                                Enumerable.Repeat((byte) 0x67, 32).ToArray(),
        //                                Enumerable.Repeat((byte) 0x00, 32).ToArray()
        //                            )
        //                        )
        //                    ),
        //                    HashUtility.Hash(
        //                        Enumerable.Repeat((byte) 0x89, Bytes32.Length).ToArray(), // graffiti
        //                        proposerSlashingsHash // proposer slashings
        //                    )
        //                ),
        //                HashUtility.Hash(
        //                    HashUtility.Hash(
        //                        attesterSlashingsHash, // attester slashings
        //                        attestationsHash // attestations
        //                    ),
        //                    HashUtility.Hash(
        //                        depositsHash, // deposits
        //                        voluntaryExitsHash // voluntary exits
        //                    )
        //                )
        //            );
        //            
        //            bytes.ToArray().ShouldBe(expected);
        //        }
    }
}
