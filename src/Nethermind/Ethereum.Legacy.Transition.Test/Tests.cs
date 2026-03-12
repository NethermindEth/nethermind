// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.Transition.Test;

[NonParallelizable]
public class BerlinToLondon : BlockchainTestFixture<BerlinToLondon>;

public class ByzantiumToConstantinopleFix : BlockchainTestFixture<ByzantiumToConstantinopleFix>;

public class EIP158ToByzantium : BlockchainTestFixture<EIP158ToByzantium>;

public class FrontierToHomestead : BlockchainTestFixture<FrontierToHomestead>;

public class HomesteadToDao : BlockchainTestFixture<HomesteadToDao>;

public class HomesteadToEIP150 : BlockchainTestFixture<HomesteadToEIP150>;

public class MergeToShanghai : LegacyBlockchainTestFixture<MergeToShanghai>;
