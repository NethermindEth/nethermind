// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

[SetUpFixture]
public class EezTestSetup
{
    [OneTimeSetUp]
    public void RegisterSystemTransactionDecoder() => TxDecoder.Instance.RegisterDecoder(EezTxType.CreateDecoder());
}
