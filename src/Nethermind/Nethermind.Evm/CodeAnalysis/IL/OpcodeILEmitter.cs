// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using static Nethermind.Evm.CodeAnalysis.IL.StackEmit;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Sigil.NonGeneric;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Ocsp;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal delegate void OpcodeILEmitterDelegate<T>(
    IVMConfig ilCompilerConfig,
    ContractMetadata contractMetadata,
    SegmentMetadata currentSegment,
    SubSegmentMetadata currentSubSegment,
    int opcodeIndex, OpcodeInfo opcodeMetadata,
    Sigil.Emit<T> method, Locals<T> localVariables, EnvLoader<T> envStateLoader,
    Dictionary<EvmExceptionType, Sigil.Label> exceptions, (Label returnLabel, Label jumpTable, Label exitLabel) returnLabel);
internal abstract class OpcodeILEmitter<T>
{
    public Dictionary<Instruction, OpcodeILEmitterDelegate<T>> opcodeEmitters = new();
    public bool AddEmitter(Instruction instruction, OpcodeILEmitterDelegate<T> emitter)
    {
        if (opcodeEmitters.ContainsKey(instruction))
        {
            return false;
        }
        opcodeEmitters.Add(instruction, emitter);
        return true;
    }

    public void ReplaceEmitter(Instruction instruction, OpcodeILEmitterDelegate<T> emitter)
    {
        opcodeEmitters[instruction] = emitter;
    }

    public void RemoveEmitter(Instruction instruction)
    {
        opcodeEmitters.Remove(instruction);
    }

    public void Emit(IVMConfig ilCompilerConfig, ContractMetadata contractMetadata, SegmentMetadata currentSegment, SubSegmentMetadata currentSubSegment, int opcodeIndex, OpcodeInfo opcodeMetadata, Sigil.Emit<T> method, Locals<T> localVariables, EnvLoader<T> envStateLoader, Dictionary<EvmExceptionType, Sigil.Label> exceptions, (Label returnLabel, Label jumpTable, Label exitLabel) exitLabels)
    {
        if (opcodeEmitters.TryGetValue(opcodeMetadata.Operation, out var emitter))
        {
            emitter(ilCompilerConfig, contractMetadata, currentSegment, currentSubSegment, opcodeIndex, opcodeMetadata, method, localVariables, envStateLoader, exceptions, exitLabels);
        }
        else
        {
            if (opcodeEmitters.TryGetValue(Instruction.INVALID, out var emitInvalidOpcode))
            {
                emitInvalidOpcode(ilCompilerConfig, contractMetadata, currentSegment, currentSubSegment, opcodeIndex, opcodeMetadata, method, localVariables, envStateLoader, exceptions, exitLabels);
            }
            else
            {
                throw new InvalidOperationException($"Opcode {opcodeMetadata.Operation} is not supported");
            }
        }
    }

    internal OpcodeILEmitterDelegate<T> emptyEmitter = (ilCompilerConfig, contractMetadata, currentSegment, currentSubSegment, opcodeIndex, opcodeMetadata, method, localVariables, envStateLoader, exceptions, exitLabels) => { };
}
