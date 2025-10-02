// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[SetUpFixture]
public class TxPoolTestsSetup
{
    [OneTimeSetUp]
    public void SetupFixture()
    {
        KzgPolynomialCommitments.InitializeAsync().Wait();
    }
}
