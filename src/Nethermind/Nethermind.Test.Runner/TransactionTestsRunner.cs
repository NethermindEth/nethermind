// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;

namespace Nethermind.Test.Runner;

/// <summary>
/// Runs EEST <c>transaction_tests</c> fixtures: decodes the raw tx bytes, validates them
/// against the named fork, and compares the outcome with the expected exception token.
/// </summary>
public class TransactionTestsRunner : TransactionTestBase
{
    public EthereumTestResult RunSingleTest(TransactionTest test)
    {
        Result result = RunTest(test);
        return new EthereumTestResult(test.Name, test.Fork, result) { Error = result.Error };
    }
}
