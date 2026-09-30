// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceSerializerTests
{
    private static readonly EthereumJsonSerializer Json = new();

    [Test]
    public void can_deserialize_deep_graph()
    {
        List<ParityLikeTxTrace>? traces = Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance));
        Assert.That(traces?.Count, Is.EqualTo(36));
    }

    [Test]
    public void cant_deserialize_deep_graph()
    {
        Func<List<ParityLikeTxTrace>?> traces = () => Deserialize(new ParityLikeTraceSerializer(LimboLogs.Instance, 128));
        Assert.That(traces, Throws.TypeOf<JsonException>());
    }

    [Test]
    public void round_trips_reverted_frame_result()
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace trace = new()
        {
            Action = new ParityTraceAction
            {
                Type = "create",
                CallType = "create",
                Error = "Reverted",
                Result = new ParityTraceResult { GasUsed = 0x11, Output = [0xde, 0xad, 0xbe, 0xef] },
            },
        };

        ParityTraceAction action = serializer.Deserialize(serializer.Serialize([trace]))![0].Action!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(action.Error, Is.EqualTo("Reverted"));
            Assert.That(action.Result?.GasUsed, Is.EqualTo(0x11UL));
            Assert.That(action.Result?.Output, Is.EqualTo(new byte[] { 0xde, 0xad, 0xbe, 0xef }));
            Assert.That(action.Result?.Address, Is.Null);
        }
    }

    [Test]
    public void round_trips_vm_trace()
    {
        ParityLikeTxTrace trace = CallTrace();
        trace.VmTrace = new ParityVmTrace
        {
            Code = [0x60, 0x00],
            Operations =
            [
                new ParityVmOperationTrace
                {
                    Cost = 3,
                    Pc = 0,
                    Used = 97,
                    Memory = new ParityMemoryChangeTrace { Offset = 1, Data = [0x01] },
                    Push = [[0x00, 0x02], [0x00], []],
                    Store = new ParityStorageChangeTrace { Key = [0x00], Value = [0x04] },
                    Sub = new ParityVmTrace { Code = [0x05], Operations = [new ParityVmOperationTrace { Cost = 2, Pc = 0, Used = 1, Push = [] }] },
                },
                new ParityVmOperationTrace { Cost = 1, Pc = 2, Halted = true },
                // A frame the tracer entered but never left keeps null operations.
                new ParityVmOperationTrace { Cost = 1, Pc = 3, Sub = new ParityVmTrace() },
                null!,
            ],
        };

        AssertRoundTrips(trace);
    }

    [Test]
    public void reads_stored_vm_memory_offset([Values("\"0x20\"", "32")] string offset)
    {
        string storedJson = """[{"blockNumber":0,"vmTrace":{"code":"0x52","ops":[{"cost":3,"ex":{"mem":{"data":"0x01","off":OFFSET},"push":[],"store":null,"used":100},"pc":0,"sub":null}]}}]""".Replace("OFFSET", offset);
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(storedJson));
        }

        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        Assert.That(serializer.Deserialize(bytes.ToArray())![0].VmTrace!.Operations[0].Memory.Offset, Is.EqualTo(32));
    }

    [Test]
    public void reads_vm_trace_at_max_call_depth_on_small_stack()
    {
        ParityLikeTxTrace trace = CallTrace();
        trace.VmTrace = CreateNestedVmTrace(VirtualMachineStatics.MaxCallDepth, []);
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        // Only reading is under test; the recursive writer needs more than 1 MiB at this depth.
        byte[] serialized = OnThread(16 * MemorySizes.MiB, () => serializer.Serialize([trace]));

        ParityVmTrace restored = OnThread(MemorySizes.MiB, () => serializer.Deserialize(serialized))![0].VmTrace!;

        int depth = 0;
        for (; restored.Operations.Count != 0; restored = restored.Operations[0].Sub)
        {
            depth++;
        }

        Assert.That(depth, Is.EqualTo(VirtualMachineStatics.MaxCallDepth));
    }

    [Test]
    public void nested_vm_decode_allocations_remain_proportional_to_trace_size([Values(0, 32, 128)] int depth)
    {
        List<ParityVmOperationTrace> leafOps = [];
        for (int i = 0; i < 2048; i++)
        {
            leafOps.Add(new ParityVmOperationTrace { Cost = 3, Used = 100, Pc = i, Push = [[0x01]] });
        }

        ParityLikeTxTrace trace = new() { VmTrace = CreateNestedVmTrace(depth, leafOps) };
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        byte[] bytes = serializer.Serialize([trace]);
        int jsonSize = Encoding.UTF8.GetByteCount(Json.Serialize(new[] { trace }));

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        List<ParityLikeTxTrace>? restored = serializer.Deserialize(bytes);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        TestContext.Out.WriteLine($"Depth: {depth}, JSON bytes: {jsonSize}, allocated bytes: {allocated}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(allocated, Is.LessThan(jsonSize * 32L));
        }
    }

    [Test]
    public void round_trips_state_diff([ValueSource(nameof(AccountChanges))] ParityAccountStateChange change)
    {
        ParityLikeTxTrace trace = CallTrace();
        trace.StateChanges = new Dictionary<Address, ParityAccountStateChange> { [Address.Zero] = change };

        AssertRoundTrips(trace);
    }

    private static IEnumerable<ParityAccountStateChange> AccountChanges()
    {
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(1, 2),
            Code = new ParityStateChange<byte[]>([0x00], [0x01]),
            // Slot 2 was set from zero, which the writer reports as a zero word; slot 3 is unchanged.
            Storage = new() { [1] = new([0x01], [0x02]), [2] = new(null, [0x05]), [3] = null!, [UInt256.MaxValue] = new([0x03], [0x04]) },
        };
        // Created and deleted: the writer derives the empty code, and marks a created account's storage as added.
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(null, 1),
            Nonce = new ParityStateChange<UInt256?>(null, 1),
            Storage = new() { [1] = new(null, [0x02]) },
        };
        yield return new()
        {
            Balance = new ParityStateChange<UInt256?>(1, null),
            Nonce = new ParityStateChange<UInt256?>(1, null),
        };
        yield return new() { Storage = [] };
    }

    [Test]
    public void round_trips_state_markers([Values("=", "+", "-", "*")] string marker)
    {
        UInt256? before = marker is "+" ? null : UInt256.Zero;
        UInt256? after = marker is "-" ? null : UInt256.MaxValue;
        byte[]? beforeBytes = marker is "+" ? null : [];
        byte[]? afterBytes = marker is "-" ? null : [0x00, 0x01, 0xff];
        ParityAccountStateChange account = new()
        {
            Balance = marker == "=" ? null : new(before, after),
            Nonce = marker == "=" ? null : new(before, after),
            Code = marker == "=" ? null : new(beforeBytes, afterBytes),
            Storage = [],
        };
        UInt256[] keys = [UInt256.MaxValue, UInt256.Zero, UInt256.One << 255];
        foreach (UInt256 key in keys)
        {
            account.Storage[key] = marker == "=" ? null! : new(beforeBytes, afterBytes);
        }

        AssertRoundTrips(new ParityLikeTxTrace { StateChanges = new() { [Address.Zero] = account } });
    }

    [TestCase(typeof(ParityVmOperationTrace), """{"cost":0,"ex":{"mem":1,"push":null,"store":null,"used":0},"pc":0,"sub":null}""")]
    [TestCase(typeof(ParityVmOperationTrace), """{"cost":0,"ex":{"mem":null,"push":null,"store":1,"used":0},"pc":0,"sub":null}""")]
    [TestCase(typeof(ParityAccountStateChange), """{"balance":{"*":42},"code":"=","nonce":"=","storage":{}}""")]
    [TestCase(typeof(ParityAccountStateChange), """{"balance":{"*":{"from":"0x1"}},"code":"=","nonce":"=","storage":{}}""")]
    [TestCase(typeof(ParityAccountStateChange), """{"balance":{"*":{"to":"0x1"}},"code":"=","nonce":"=","storage":{}}""")]
    public void rejects_malformed_values(Type type, string json) =>
        Assert.That(() => JsonSerializer.Deserialize(json, type, EthereumJsonSerializer.JsonOptions), Throws.InstanceOf<JsonException>());

    private static ParityLikeTxTrace CallTrace() => new()
    {
        Output = [],
        Action = new ParityTraceAction
        {
            Type = "call",
            CallType = "call",
            From = Address.Zero,
            To = Address.Zero,
            Result = new ParityTraceResult { GasUsed = 1, Output = [] },
        },
    };

    private static ParityVmTrace CreateNestedVmTrace(int depth, IReadOnlyList<ParityVmOperationTrace> leafOps)
    {
        ParityVmTrace vm = new() { Code = [], Operations = leafOps };
        for (int i = 0; i < depth; i++)
        {
            vm = new ParityVmTrace { Code = [0xf1], Operations = [new ParityVmOperationTrace { Cost = 1, Used = 2, Push = [], Sub = vm }] };
        }

        return vm;
    }

    // The stored form drops what the RPC form does, such as leading zeros, so compare what each would return.
    private static void AssertRoundTrips(ParityLikeTxTrace trace)
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        List<ParityLikeTxTrace>? restored = serializer.Deserialize(serializer.Serialize([trace]));
        Assert.That(Json.Serialize(restored), Is.EqualTo(Json.Serialize(new[] { trace })));
    }

    private static T OnThread<T>(int maxStackSize, Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        Thread thread = new(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }, maxStackSize);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
