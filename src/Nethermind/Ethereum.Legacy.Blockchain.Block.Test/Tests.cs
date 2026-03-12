// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Legacy.Blockchain.Block.Test;

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
