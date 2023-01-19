// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs;

public class WithdrawalSszTrieTests
{

    [Test]
    public void Test_multipleSszs()
    {
        Withdrawal[] withdrawals = new Withdrawal[] { new()
            {
                Index = 0,
                ValidatorIndex = 65535,
                Address =  new Address("0000000000000000000000000000000000000000"),
                AmountInGwei =  0,
            },
            new(){
                Index = 1,
                ValidatorIndex = 65536,
                Address =  new Address("0100000000000000000000000000000000000000"),
                AmountInGwei =  04523128485832663883,
            },
            new(){
                Index = 2,
                ValidatorIndex = 65537,
                Address =  new Address("0200000000000000000000000000000000000000"),
                AmountInGwei =  09046256971665327767,
            },
            new(){
                Index = 4,
                ValidatorIndex = 65538,
                Address =  new Address("0300000000000000000000000000000000000000"),
                AmountInGwei =  13569385457497991651,
            },
            new(){
                Index = 4,
                ValidatorIndex = 65539,
                Address =  new Address("0400000000000000000000000000000000000000"),
                AmountInGwei =  18446744073709551615,
            },
            new(){
                Index = 5,
                ValidatorIndex = 65540,
                Address =  new Address("0500000000000000000000000000000000000000"),
                AmountInGwei =  02261564242916331941,
            },
            new(){
                Index = 6,
                ValidatorIndex = 65541,
                Address =  new Address("0600000000000000000000000000000000000000"),
                AmountInGwei =  02713877091499598330,
            },
            new(){
                Index = 7,
                ValidatorIndex = 65542,
                Address =  new Address("0700000000000000000000000000000000000000"),
                AmountInGwei =  03166189940082864718,
            },};
        Merkle.Ize(out UInt256 root, withdrawals);
        Assert.AreEqual(UInt256.Parse("bd97f65e513f870484e85927510acb291fcfb3e593c05ab7f21f206921264946", System.Globalization.NumberStyles.HexNumber), root);
    }


    [Test]
    public void Root_experiment()
    {
        WithdrawalSszTrie trie = new(new List<Withdrawal>()
        {
            new Withdrawal()
            {
                Index = 17107150653359250726,
                ValidatorIndex = 1906681273455760070,
                Address = new Address("0x02ab1379b6334b58df82c85d50ff1214663cba20"),
                AmountInGwei = 5055030296454530815
            }
        });
        Assert.AreEqual(new Keccak("0xed9cec6fb8ee22b146059d02c38940cca1dd22a00d0132b000999b983fceff95"),trie.Root);
        // f2c455ad181342d83abbc0dc5a5d9b22f97f84c048c06d1bee7016eb5e37f741 - EthereumJS
        // 0xed9cec6fb8ee22b146059d02c38940cca1dd22a00d0132b000999b983fceff95 - Consensus reference tests https://media.githubusercontent.com/media/ethereum/consensus-spec-tests/v1.3.0-rc.1/tests/mainnet/capella/ssz_static/Withdrawal/ssz_random/case_0/roots.yaml
    }

    [Test]
    public void Root_experiment_rlp()
    {
        WithdrawalTrie trie = new WithdrawalTrie(new List<Withdrawal>()
        {
            new Withdrawal() { Index = 10078475495033652149, ValidatorIndex = 3916426429657093836, Address = new Address("0x7f16ebcc35e62c99c7c545585d37c8a9d09e3a2a"), AmountInGwei = 12260575381911018860 }
        });
    }
}
