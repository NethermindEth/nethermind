// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Legacy.VM.Test;

// [TestFixture] required on at least one concrete class in the assembly for NUnit discovery
// to find test methods inherited from generic base classes in external assemblies.
[TestFixture]
public class MetaTests : DirectoryMetaTests<VmPrefix>;

public class ArithmeticTest : LegacyStateTestFixture<ArithmeticTest, VmPrefix>;

public class Tests : LegacyStateTestFixture<Tests, VmPrefix>;

public class BitwiseLogicOperation : LegacyStateTestFixture<BitwiseLogicOperation, VmPrefix>;

public class IOAndFlowOperations : LegacyStateTestFixture<IOAndFlowOperations, VmPrefix>;

public class LogTest : LegacyStateTestFixture<LogTest, VmPrefix>;

public class Performance : LegacyRetryStateTestFixture<Performance, VmPrefix>;
