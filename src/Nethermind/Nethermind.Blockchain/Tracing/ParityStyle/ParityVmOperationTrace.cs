// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

//        {
//            "cost": 0.0,
//            "ex": {
//                "mem": null,
//                "push": [],
//                "store": null,
//                "used": 16961.0
//            },
//            "pc": 526.0,
//            "sub": null
//        }
[JsonConverter(typeof(ParityVmOperationTraceConverter))]
public class ParityVmOperationTrace
{
    public ulong Cost { get; set; }
    public ParityMemoryChangeTrace Memory { get; set; }
    public byte[][] Push { get; set; }
    public ParityStorageChangeTrace Store { get; set; }
    public ulong Used { get; set; }
    public int Pc { get; set; }

    /// <summary>The opcode's mnemonic, such as <c>PUSH1</c>, or <see langword="null"/> for a synthetic operation, such as one
    /// heading an EIP-8141 frame, and for one read from a store written without it.</summary>
    public string? Op { get; set; }

    public ParityVmTrace Sub { get; set; }

    /// <summary>The operation began executing and halted exceptionally, so it is serialized with a null <c>ex</c>.</summary>
    public bool Halted { get; set; }
}
