// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Test;

public class TraceSerializerTests
{
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
    public void round_trips_stored_trace_families([Values("trace", "empty-state", "vm", "vm-op", "state", "state-storage")] string family)
    {
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        ParityLikeTxTrace trace = new()
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
        if (family is "vm" or "vm-op")
        {
            trace.VmTrace = new ParityVmTrace { Code = [0x00], Operations = [] };
            if (family == "vm-op")
            {
                trace.VmTrace.Operations = [new ParityVmOperationTrace
                {
                    Cost = 3, Pc = 0, Used = 2,
                    Memory = new ParityMemoryChangeTrace { Offset = 1, Data = [0x01] },
                    Push = [[0x02]],
                    Store = new ParityStorageChangeTrace { Key = [0x03], Value = [0x04] },
                    Sub = new ParityVmTrace { Code = [0x05], Operations = [] },
                }, new ParityVmOperationTrace { Cost = 1, Pc = 1, Halted = true }];
            }
        }
        if (family == "empty-state")
        {
            trace.StateChanges = [];
        }
        if (family is "state" or "state-storage")
        {
            trace.StateChanges = new Dictionary<Address, ParityAccountStateChange>
            {
                [Address.Zero] = new()
                {
                    Code = new ParityStateChange<byte[]>([0x00], [0x01]),
                    Balance = new ParityStateChange<UInt256?>(new UInt256(1), new UInt256(2)),
                    Nonce = new ParityStateChange<UInt256?>(new UInt256(3), new UInt256(4)),
                    Storage = family == "state-storage" ? new Dictionary<UInt256, ParityStateChange<byte[]>>
                    {
                        [new UInt256(1)] = new([0x01], [0x02]),
                    } : null,
                },
            };
        }

        byte[] stored = serializer.Serialize([trace]);
        List<ParityLikeTxTrace>? restored = serializer.Deserialize(stored);
        EthereumJsonSerializer json = new();
        Assert.That(json.Serialize(restored), Is.EqualTo(json.Serialize(new[] { trace })));
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
        ParityLikeTxTrace trace = new() { StateChanges = new() { [Address.Zero] = account } };
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        EthereumJsonSerializer json = new();
        Assert.That(json.Serialize(serializer.Deserialize(serializer.Serialize([trace]))), Is.EqualTo(json.Serialize(new[] { trace })));
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
    public void round_trips_deep_vm([Values(32, 128, 512, 1024)] int depth)
    {
        // The existing recursive VM writer needs more than the default Windows thread stack at the EVM depth limit.
        Exception? caught = null;
        Thread thread = new(() =>
        {
            try
            {
                ParityVmTrace vm = CreateNestedVmTrace(depth, []);
                ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
                ParityVmTrace restored = serializer.Deserialize(serializer.Serialize([new ParityLikeTxTrace { VmTrace = vm }]))![0].VmTrace!;
                int restoredDepth = 0;
                while (restored.Operations.Count != 0)
                {
                    restoredDepth++;
                    restored = restored.Operations[0].Sub;
                }
                Assert.That(restoredDepth, Is.EqualTo(depth));
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        }, maxStackSize: 4 * MemorySizes.MiB);
        thread.Start();
        thread.Join();
        if (caught is not null) throw caught;
    }

    [Test]
    public void nested_vm_decode_allocations_remain_proportional_to_trace_size([Values(0, 32, 128)] int depth)
    {
        List<ParityVmOperationTrace> leafOps = [];
        for (int i = 0; i < 2048; i++)
        {
            leafOps.Add(new ParityVmOperationTrace { Cost = 3, Used = 100, Pc = i, Push = [[0x01]] });
        }
        ParityVmTrace vm = CreateNestedVmTrace(depth, leafOps);
        ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
        byte[] bytes = serializer.Serialize([new ParityLikeTxTrace { VmTrace = vm }]);
        EthereumJsonSerializer json = new();
        int jsonSize = Encoding.UTF8.GetByteCount(json.Serialize(new[] { new ParityLikeTxTrace { VmTrace = vm } }));
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

    private static ParityVmTrace CreateNestedVmTrace(int depth, IReadOnlyList<ParityVmOperationTrace> leafOps)
    {
        ParityVmTrace vm = new() { Code = [], Operations = leafOps };
        for (int i = 0; i < depth; i++)
        {
            vm = new ParityVmTrace { Code = [0xf1], Operations = [new ParityVmOperationTrace { Cost = 1, Used = 2, Push = [], Sub = vm }] };
        }
        return vm;
    }

    private List<ParityLikeTxTrace>? Deserialize(ITraceSerializer<ParityLikeTxTrace> serializer)
    {
        Type type = GetType();
        using Stream stream = type.Assembly.GetManifestResourceStream($"{type.Assembly.GetName().Name}.xdai-17600039.json")!;
        List<ParityLikeTxTrace>? traces = serializer.Deserialize(stream);
        return traces;
    }
}
