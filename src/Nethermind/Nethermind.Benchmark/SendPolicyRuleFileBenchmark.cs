// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.SendPolicy.Plugin;
using Testably.Abstractions;

namespace Nethermind.Benchmarks;

[MemoryDiagnoser]
public class SendPolicyRuleFileBenchmark
{
    private const int ReadsPerInvocation = 64;
    private readonly IFileSystem _fileSystem = new RealFileSystem();
    private string _path = null!;
    private string _contents = null!;
    private IContainer _container = null!;
    private SendPolicyRuleFile _rules = null!;

    [Params(256, 65536)]
    public int FileSize { get; set; }

    [Params(1, 8)]
    public int Workers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.GetTempFileName();
        string policy = "from 0x1111111111111111111111111111111111111111\n#";
        _contents = policy + new string('x', FileSize - policy.Length);
        File.WriteAllText(_path, _contents);
        _container = new ContainerBuilder()
            .AddModule(new SendPolicyModule())
            .AddSingleton<ISendPolicyConfig>(new SendPolicyConfig { Enabled = true, RulesPath = _path })
            .AddSingleton<IFileSystem>(_fileSystem)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();
        _rules = _container.Resolve<SendPolicyRuleFile>();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ReadsPerInvocation)]
    public void ReadToEnd() => ReadAll(optimized: false);

    [Benchmark(OperationsPerInvoke = ReadsPerInvocation)]
    public void CompareChunks() => ReadAll(optimized: true);

    private void ReadAll(bool optimized)
    {
        if (Workers == 1) ReadWorker(optimized);
        else Parallel.For(0, Workers, _ => ReadWorker(optimized));
    }

    private void ReadWorker(bool optimized)
    {
        for (int i = 0; i < ReadsPerInvocation / Workers; i++)
        {
            if (optimized)
            {
                if (_rules.Current.Error is not null) throw new InvalidOperationException();
            }
            else
            {
                using Stream stream = _fileSystem.File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                if (reader.ReadToEnd() != _contents) throw new InvalidOperationException();
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        File.Delete(_path);
    }
}
