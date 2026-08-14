// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Ethereum.Test.Base;

public class LoadEipTestsStrategy()
    : TestLoadStrategy(Path.Combine("EIPTests", "StateTests"), TestType.State);
