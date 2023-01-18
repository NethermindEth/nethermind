// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.Store.Test.Proofs;

public class WithdrawalSszTrieTests
{
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
