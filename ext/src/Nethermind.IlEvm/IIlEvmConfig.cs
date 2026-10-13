// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using Nethermind.Config;
using Nethermind.Core.Container;

namespace Nethermind.IlEvm;

/// <summary>Where compiled code may run.</summary>
public enum IlEvmScope
{
    /// <summary>Only the environments serving read-only queries; block processing always runs on the interpreter.</summary>
    Rpc,
    /// <summary>Every machine the container builds, block processing and production included. Experimental.</summary>
    All,
}

[ConfigCategory(Description = "IL-EVM: compiles hot EVM contracts to native code for faster read-only calls.", HiddenFromDocs = true)]
public interface IIlEvmConfig : IConfig
{
    [ConfigItem(Description = "Enables IL-EVM.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Rpc: only read-only query environments run compiled code. All: every machine, block processing included (experimental).", DefaultValue = "Rpc")]
    IlEvmScope Scope { get; set; }

    [ConfigItem(Description = "The read-only queries accelerated in Rpc scope: Call (eth_call, eth_estimateGas, eth_createAccessList), Simulate (eth_simulateV1), Trace (debug_*/trace_* with non-instruction tracers).", DefaultValue = "Call")]
    ReadOnlyQueryKind Surfaces { get; set; }

    [ConfigItem(Description = "Frames a contract runs on the interpreter before it is compiled.", DefaultValue = "32")]
    int Threshold { get; set; }

    [ConfigItem(Description = "Opcodes a contract runs on the interpreter, over all its frames, before it is compiled.", DefaultValue = "20000")]
    int OpcodeThreshold { get; set; }

    [ConfigItem(Description = "Interpreted opcodes per second a contract must also run before it is compiled, so rarely run code stays on the interpreter; 0 disables.", DefaultValue = "50000")]
    int HotOpcodesPerSecond { get; set; }

    [ConfigItem(Description = "Compile on the thread that makes a contract hot instead of in the background. For tests and benchmarks.", DefaultValue = "false", HiddenFromDocs = true)]
    bool CompileSynchronously { get; set; }
}

public class IlEvmConfig : IIlEvmConfig
{
    public bool Enabled { get; set; }
    public IlEvmScope Scope { get; set; } = IlEvmScope.Rpc;
    public ReadOnlyQueryKind Surfaces { get; set; } = ReadOnlyQueryKind.Call;
    public int Threshold { get; set; } = 32;
    public int OpcodeThreshold { get; set; } = 20_000;
    public int HotOpcodesPerSecond { get; set; } = 50_000;
    public bool CompileSynchronously { get; set; }
}
