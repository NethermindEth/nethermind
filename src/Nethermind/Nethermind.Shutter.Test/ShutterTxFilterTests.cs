// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Merge.Plugin.Test;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterTxFilterTests : EngineModuleTests
{
    [TestCase("f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0df1cd95e75d0188cded14137c9c83a3ce6d710886bf9139a10cab20dd693ab85a020f5fdae2704bf133be02897c886ceb9189a9ea363989b11330461a78b9bb368")]
    [TestCase("02f8758227d81385012a05f20085012a05f2088252089497d2eeb65da0c37dc0f43ff4691e521673efadfd872386f26fc1000080c080a0c00874a71afda5444b961f78774196fbb833c33482d6463b97380147dd7d472fa061d508b02cb212c78d0b864a04b10e0b0e3accca6e08252049d999c4629cd9a8")]
    public void Accepts_valid_transaction(string txHex)
    {
        Assert.That(IsAllowed(txHex));
    }

    [TestCase("f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd5a0b806b9e17c30c4eaad51b290714a407925c82818311a432e4ee656ad23938852a045cd3f087a1f2580ba7d806fa6ba2bfc9933b317ee89fa67713665aab7c22441")]
    public void Rejects_wrong_chain_id(string txHex)
    {
        Assert.That(!IsAllowed(txHex));
    }

    [TestCase("f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a09999999999999999999999999999999999999999999999999999999999999999a09999999999999999999999999999999999999999999999999999999999999999")]
    public void Rejects_bad_signature(string txHex)
    {
        Assert.That(!IsAllowed(txHex));
    }

    private bool IsAllowed(string txHex)
    {
        ShutterTxFilter txFilter = new(ChiadoSpecProvider.Instance, LimboLogs.Instance);
        Transaction tx = Rlp.Decode<Transaction>(Convert.FromHexString(txHex));
        return txFilter.IsAllowed(tx, Build.A.BlockHeader.TestObject);
    }
}
