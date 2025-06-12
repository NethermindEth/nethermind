// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using VerifyNUnit;
using System.Diagnostics;

namespace Nethermind.Evm.Test.ILEVM;

public class ContractTestTimer : IDisposable
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly int _operations;
    private readonly bool _useIlEvm;
    private readonly string _name;


    public ContractTestTimer(string name, int operations, bool useIlEvm = false)
    {
        _name = name;
        _operations = operations;
        _useIlEvm = useIlEvm;
    }


    public void Dispose()
    {
        _stopwatch.Stop();
        double timePerOperation = (double)_stopwatch.ElapsedMilliseconds / (double)_operations;
        Console.WriteLine($"{_name}, using IL EVM: {_useIlEvm}, took {_stopwatch.ElapsedMilliseconds} ms for {_operations} operations, which is {timePerOperation} ms per operation.");
    }
}
