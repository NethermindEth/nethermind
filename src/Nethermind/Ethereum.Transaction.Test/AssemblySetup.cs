// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Crypto;
using NUnit.Framework;

// Assembly-level setup fixture: no namespace so NUnit treats it as the root.
// Runs OneTimeSetUp before any test in the assembly begins, ensuring KZG trusted-setup
// is loaded while the thread pool is idle. Without this, KzgPolynomialCommitments
// initialisation fires mid-execution of the 171 parallel TransactionTests, which
// saturates the thread pool and causes Task.Run(...).Wait() in
// GeneralStateTestBase's static constructor to stall for 13+ minutes.
[SetUpFixture]
public class AssemblySetup
{
    [OneTimeSetUp]
    public Task InitializeKzg() => KzgPolynomialCommitments.InitializeAsync();
}
