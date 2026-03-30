// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Blockchain.Block.Test;

// [TestFixture] required on at least one concrete class in the assembly for NUnit discovery
// to find test methods inherited from generic base classes in external assemblies.
[TestFixture]
public class BlockGasLimitTest : LegacyBlockchainTestFixture<BlockGasLimitTest>;

public class ExploitTest : LegacyBlockchainTestFixture<ExploitTest>;

public class ForgedTest : LegacyBlockchainTestFixtureNoRlpValidation<ForgedTest>;

public class ForkStressTest : LegacyBlockchainTestFixture<ForkStressTest>;

public class GasPricerTest : LegacyBlockchainTestFixture<GasPricerTest>;

public class InvalidHeaderTest : LegacyBlockchainTestFixture<InvalidHeaderTest>;

public class MultiChainTest : LegacyBlockchainTestFixture<MultiChainTest>;

public class RandomBlockhashTest : LegacyBlockchainTestFixture<RandomBlockhashTest>;

public class StateTests : LegacyBlockchainTestFixture<StateTests>;

public class TotalDifficultyTest : LegacyBlockchainTestFixture<TotalDifficultyTest>;

public class UncleHeaderValidity : LegacyBlockchainTestFixture<UncleHeaderValidity>;

public class UncleSpecialTests : LegacyBlockchainTestFixture<UncleSpecialTests>;

public class UncleTest : LegacyBlockchainTestFixture<UncleTest>;

public class ValidBlockTest : LegacyBlockchainTestFixture<ValidBlockTest>;

public class WalletTest : LegacyBlockchainTestFixture<WalletTest>;
