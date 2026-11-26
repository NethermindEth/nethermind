// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Evm;

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
    public ParityVmTrace Sub { get; set; }

    /// <summary>The opcode of an operation whose <c>pc</c> lies outside its trace's code, such as an EIP-8141 frame root operation.</summary>
    public Instruction? Op { get; set; }

    /// <summary>The operation began executing and halted exceptionally, so it is serialized with a null <c>ex</c>.</summary>
    public bool Halted { get; set; }
}
