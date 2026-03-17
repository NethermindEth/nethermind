// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;

namespace Ethereum.Test.Base;

public class LoadEofTestsStrategy()
    : TestLoadStrategy(Path.Combine("EIPTests", "StateTests", "stEOF"), TestType.State);
