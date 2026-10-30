// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Nethermind.Evm.Fody;

internal static class GuestDispatchRewriter
{
    internal static void Rewrite(TypeDefinition dispatch)
    {
        MethodDefinition? marker = null;
        MethodDefinition? template = null;
        foreach (MethodDefinition method in dispatch.Methods)
        {
            if (method.Name == "TailDispatch")
            {
                if (marker is not null) throw new WeavingException("Multiple guest tail dispatch markers.");
                marker = method;
            }
            if (method.Name == "ExecuteOpcode") template = method;
        }
        if (marker is null) return;
        if (template is null || !marker.IsPrivate || marker.HasThis || marker.HasGenericParameters
            || marker.CallingConvention != MethodCallingConvention.Default
            || marker.Parameters.Count != 9 || marker.Parameters[8].ParameterType.MetadataType != MetadataType.IntPtr
            || !MatchesHandler(marker, template))
            throw new WeavingException("Invalid guest tail dispatch marker signature.");

        int rewritten = 0;
        foreach (TypeDefinition type in dispatch.Module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                ILProcessor il = method.Body.GetILProcessor();
                bool simplified = false;
                for (int i = 0; i < method.Body.Instructions.Count; i++)
                {
                    Instruction instruction = method.Body.Instructions[i];
                    if (instruction.Operand is not MethodReference reference || reference.Name != marker.Name
                        || reference.DeclaringType.GetElementType().FullName != dispatch.FullName) continue;
                    if (reference.Resolve() != marker || instruction.OpCode != OpCodes.Call
                        || method.DeclaringType != dispatch || !MatchesHandler(marker, method)
                        || method.Body.HasExceptionHandlers || !ReturnsCallResult(instruction.Next))
                        throw new WeavingException($"Unsupported guest tail dispatch use in {method.FullName}.");

                    // Each rewrite grows the body, which can push a short branch's target out of its range; branches
                    // take their long forms while the body changes and the shortest that fits once it is done.
                    if (!simplified)
                    {
                        method.Body.SimplifyMacros();
                        simplified = true;
                    }

                    // Use the call site's constructed types, including its enclosing gas-policy argument.
                    CallSite signature = new(reference.ReturnType) { CallingConvention = MethodCallingConvention.Default };
                    for (int parameter = 0; parameter < reference.Parameters.Count - 1; parameter++)
                        signature.Parameters.Add(new ParameterDefinition(reference.Parameters[parameter].ParameterType));
                    instruction.OpCode = OpCodes.Tail;
                    instruction.Operand = null;
                    Instruction call = il.Create(OpCodes.Calli, signature);
                    il.InsertAfter(instruction, call);
                    il.InsertAfter(call, il.Create(OpCodes.Ret));
                    i += 2;
                    rewritten++;
                }

                if (simplified) method.Body.OptimizeMacros();
            }

        if (rewritten == 0) throw new WeavingException("Guest tail dispatch marker has no callers.");
        // Every operand referring to the marker was either rewritten or rejected above.
        dispatch.Methods.Remove(marker);
    }

    private static bool MatchesHandler(MethodDefinition marker, MethodDefinition handler)
    {
        if (handler.HasThis || handler.ReturnType.FullName != marker.ReturnType.FullName
            || handler.Parameters.Count != marker.Parameters.Count - 1) return false;
        for (int i = 0; i < handler.Parameters.Count; i++)
            if (handler.Parameters[i].ParameterType.FullName != marker.Parameters[i].ParameterType.FullName) return false;
        return true;
    }

    private static bool ReturnsCallResult(Instruction? instruction)
    {
        // Debug builds store the result and branch to a shared return epilogue; release builds usually return directly.
        int? result = null;
        for (int steps = 0; instruction is not null && steps < 16; steps++)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Nop: break;
                case Code.Br:
                case Code.Br_S:
                    instruction = (Instruction)instruction.Operand;
                    continue;
                case Code.Stloc_0: if (result is not null) return false; result = 0; break;
                case Code.Stloc_1: if (result is not null) return false; result = 1; break;
                case Code.Stloc_2: if (result is not null) return false; result = 2; break;
                case Code.Stloc_3: if (result is not null) return false; result = 3; break;
                case Code.Stloc:
                case Code.Stloc_S:
                    if (result is not null) return false;
                    result = ((VariableDefinition)instruction.Operand).Index;
                    break;
                case Code.Ldloc_0: return result == 0 && instruction.Next?.OpCode == OpCodes.Ret;
                case Code.Ldloc_1: return result == 1 && instruction.Next?.OpCode == OpCodes.Ret;
                case Code.Ldloc_2: return result == 2 && instruction.Next?.OpCode == OpCodes.Ret;
                case Code.Ldloc_3: return result == 3 && instruction.Next?.OpCode == OpCodes.Ret;
                case Code.Ldloc:
                case Code.Ldloc_S:
                    return result == ((VariableDefinition)instruction.Operand).Index && instruction.Next?.OpCode == OpCodes.Ret;
                case Code.Ret: return result is null;
                default: return false;
            }
            instruction = instruction.Next;
        }
        return false;
    }
}
