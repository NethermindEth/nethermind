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
                Index = 10078475495033652149,
                ValidatorIndex = 3916426429657093836,
                Address = new Address("0x7f16ebcc35e62c99c7c545585d37c8a9d09e3a2a"),
                Amount = 12260575381911018860
            }
        });

    }

    [Test]
    public void Root_experiment_rlp()
    {
        WithdrawalTrie trie = new WithdrawalTrie(new List<Withdrawal>()
        {
            new Withdrawal() { Index = 10078475495033652149, ValidatorIndex = 3916426429657093836, Address = new Address("0x7f16ebcc35e62c99c7c545585d37c8a9d09e3a2a"), Amount = 12260575381911018860 }
        });
    }
}
