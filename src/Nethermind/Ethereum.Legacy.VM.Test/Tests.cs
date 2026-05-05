// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;

namespace Ethereum.Legacy.VM.Test;

public class MetaTests : DirectoryMetaTests<VmPrefix>;

public class ArithmeticTest : LegacyStateTestFixture<ArithmeticTest, VmPrefix>;

public class Tests : LegacyStateTestFixture<Tests, VmPrefix>;

public class BitwiseLogicOperation : LegacyStateTestFixture<BitwiseLogicOperation, VmPrefix>;

public class IOAndFlowOperations : LegacyStateTestFixture<IOAndFlowOperations, VmPrefix>;

public class LogTest : LegacyStateTestFixture<LogTest, VmPrefix>;

public class Performance : LegacyRetryStateTestFixture<Performance, VmPrefix>;
