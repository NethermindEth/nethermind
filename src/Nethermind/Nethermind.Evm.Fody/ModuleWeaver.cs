// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Nethermind.Evm.Fody;

/// <summary>Gives opcode specializations distinct profiler-visible entry points.</summary>
public sealed class ModuleWeaver : BaseModuleWeaver
{
    private readonly Dictionary<(MethodDefinition Template, string Opcode), MethodDefinition> _factories = [];
    private readonly Dictionary<string, MethodDefinition> _handlers = [];

    /// <inheritdoc />
    public override IEnumerable<string> GetAssembliesForScanning() => Array.Empty<string>();

    /// <inheritdoc />
    public override void Execute()
    {
        TypeDefinition vm = ModuleDefinition.GetType("Nethermind.Evm.VirtualMachine`1")
            ?? throw new WeavingException("VirtualMachine type was not found.");
        MethodDefinition handler = GetMethod(vm, "ExecuteOpcode");
        bool hasTailCall = false;
        foreach (Instruction instruction in handler.Body.Instructions)
            if (instruction.OpCode == OpCodes.Tail) { hasTailCall = true; break; }
        if (!hasTailCall)
            throw new WeavingException("Opcode naming must run after InlineIL and preserve the dispatch tail call.");

        MethodDefinition[] methods = new MethodDefinition[vm.Methods.Count];
        vm.Methods.CopyTo(methods, 0);
        foreach (MethodDefinition method in methods)
        {
            // CALL/CREATE carry an opcode type through several fork-selection factories.
            // Clone that chain from its concrete entry point before redirecting its leaf.
            if (!method.HasBody || IsFactory(method.Name)) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not GenericInstanceMethod call || !IsFactory(call.Name)
                    || call.DeclaringType.Resolve() != vm) continue;
                string name = call.Name switch
                {
                    "JumpIfOpcodeHandler" => "JumpI",
                    "GetCallHandler" or "GetCreateHandler" => GetOperationName(call.GenericArguments[0]),
                    _ => GetOpcodeName(call.GenericArguments[0])
                };
                instruction.Operand = Retarget(call, CloneFactory(Resolve(call), name));
            }
        }

        HashSet<string> names = new(_handlers.Keys, StringComparer.OrdinalIgnoreCase);
        if (names.Count != _handlers.Count)
            throw new WeavingException("Named handlers differ only by case.");
        TypeDefinition instructionType = ModuleDefinition.GetType("Nethermind.Evm.Instruction")
            ?? throw new WeavingException("Instruction enum was not found.");
        foreach (FieldDefinition opcode in instructionType.Fields)
            if (opcode.HasConstant && !names.Remove(opcode.Name))
                throw new WeavingException($"No named handler for opcode {opcode.Name}.");
        if (!names.SetEquals(new[] { "BadInstruction" }))
            throw new WeavingException("Named handlers do not match the instruction enum.");
        VerifyTableHandlers(vm);
        RemoveUnusedFactories(vm, methods);
        WriteInfo($"Named {_handlers.Count} opcode handlers without adding runtime calls.");
    }

    private static void RemoveUnusedFactories(TypeDefinition vm, MethodDefinition[] originals)
    {
        HashSet<MethodDefinition> factories = [];
        foreach (MethodDefinition method in originals)
            if (IsFactory(method.Name)) factories.Add(method);
        foreach (TypeDefinition type in vm.Module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody || factories.Contains(method)) continue;
                foreach (Instruction instruction in method.Body.Instructions)
                    if (instruction.Operand is MethodReference reference && IsFactory(reference.Name)
                        && reference.DeclaringType.Resolve() == vm
                        && factories.Contains(Resolve(reference)))
                        throw new WeavingException($"Method {method.FullName} still references an original opcode factory.");
            }
        foreach (MethodDefinition factory in factories) vm.Methods.Remove(factory);
    }

    private void VerifyTableHandlers(TypeDefinition vm)
    {
        Stack<MethodDefinition> pending = [];
        HashSet<MethodDefinition> visited = [];
        HashSet<MethodDefinition> expected = [.. _handlers.Values];
        HashSet<MethodDefinition> actual = [];
        pending.Push(GetMethod(vm, "GenerateOpcodeHandlers"));
        while (pending.Count != 0)
        {
            MethodDefinition method = pending.Pop();
            if (!visited.Add(method) || !method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference reference
                    || reference.DeclaringType.GetElementType().FullName != vm.FullName
                    || reference.DeclaringType.Resolve() != vm) continue;
                MethodDefinition target = Resolve(reference);
                if (instruction.OpCode == OpCodes.Ldftn)
                {
                    if (expected.Contains(target)) actual.Add(target);
                    else if (target.Name is "ExecuteOpcode" or "ExecuteJumpIfOpcode")
                        throw new WeavingException($"Opcode table still references unnamed handler {target.Name}.");
                }
                else
                {
                    pending.Push(target);
                }
            }
        }
        if (!actual.SetEquals(expected))
            throw new WeavingException("Named opcode handlers are not all reachable from the opcode table factories.");
    }

    private static MethodDefinition GetMethod(TypeDefinition type, string name)
    {
        MethodDefinition? result = null;
        foreach (MethodDefinition method in type.Methods)
        {
            if (method.Name != name) continue;
            if (result is not null) throw new WeavingException($"Multiple methods named {name} in {type.FullName}.");
            result = method;
        }
        return result ?? throw new WeavingException($"Method {name} was not found in {type.FullName}.");
    }

    private static MethodDefinition Resolve(MethodReference reference) =>
        reference.Resolve() ?? throw new WeavingException($"Could not resolve {reference.FullName}.");

    private static bool IsFactory(string name) => name is
        "OpcodeHandler" or "TerminatingOpcodeHandler" or "JumpIfOpcodeHandler" or "GetCallHandler" or "GetCreateHandler";

    private MethodDefinition CloneFactory(MethodDefinition source, string opcode)
    {
        if (_factories.TryGetValue((source, opcode), out MethodDefinition? existing)) return existing;
        MethodDefinition factory = new MethodCloner(source, WriteWarning).Clone(source.Name + "_" + opcode);
        _factories.Add((source, opcode), factory);
        source.DeclaringType.Methods.Add(factory);
        bool redirected = false;
        foreach (Instruction instruction in factory.Body.Instructions)
        {
            if (instruction.Operand is not GenericInstanceMethod target || target.DeclaringType.Resolve() != source.DeclaringType)
                continue;
            if (IsFactory(target.Name))
            {
                instruction.Operand = Retarget(target, CloneFactory(Resolve(target), opcode));
                redirected = true;
            }
            else if (instruction.OpCode == OpCodes.Ldftn && target.Name is "ExecuteOpcode" or "ExecuteJumpIfOpcode")
            {
                if (!_handlers.TryGetValue(opcode, out MethodDefinition? handler))
                {
                    // Retain the generic parameters: only the metadata name and table target change.
                    handler = new MethodCloner(Resolve(target), WriteWarning).Clone("Op" + opcode);
                    source.DeclaringType.Methods.Add(handler);
                    _handlers.Add(opcode, handler);
                }
                instruction.Operand = Retarget(target, handler);
                redirected = true;
            }
        }
        if (!redirected) throw new WeavingException($"Opcode factory {source.FullName} no longer selects a dispatch handler.");
        return factory;
    }

    private static string GetOperationName(TypeReference type)
    {
        string name = type.Name.Split('`')[0];
        if (type is GenericParameter || !name.StartsWith("Op", StringComparison.Ordinal))
            throw new WeavingException($"Expected a concrete opcode operation, found {type.FullName}.");
        return name.Substring(2);
    }

    private static string GetOpcodeName(TypeReference type)
    {
        string name = type.Name.Split('`')[0];
        if (type is GenericParameter || !name.EndsWith("Opcode", StringComparison.Ordinal))
            throw new WeavingException($"Unknown opcode body {type.FullName}.");
        name = name.Substring(0, name.Length - "Opcode".Length);
        if (name is "Math1" or "Math2" or "Math3" or "Bitwise" or "Shift"
            or "EnvAddress" or "Env32Bytes" or "EnvUInt256" or "EnvUInt32" or "EnvUInt64"
            or "BlkAddress" or "BlkUInt256" or "BlkUInt64" or "Push" or "Dup" or "Swap" or "Log")
        {
            // These bodies are nested in VirtualMachine<TGasPolicy>; argument zero is the enclosing gas policy.
            string operation = GetOperationName(((GenericInstanceType)type).GenericArguments[1]);
            if (name is "Push" or "Dup" or "Swap" or "Log") return name + operation;
            return operation.StartsWith("Bitwise", StringComparison.Ordinal) ? operation.Substring("Bitwise".Length) : operation;
        }
        return name switch
        {
            "CountLeadingZeros" => "Clz",
            "ProgramCounter" => "Pc",
            "Keccak" => "Keccak256",
            "SStoreMetered" or "SStoreUnmetered" => "SStore",
            _ => name
        };
    }

    internal static GenericInstanceMethod Retarget(GenericInstanceMethod original, MethodDefinition target)
    {
        if (target.GenericParameters.Count != original.GenericArguments.Count)
            throw new WeavingException($"Handler {target.Name} does not match the dispatch arity of {original.ElementMethod.FullName}.");
        MethodDefinition template = Resolve(original);
        if (target.HasThis != template.HasThis || target.ExplicitThis != template.ExplicitThis
            || target.CallingConvention != template.CallingConvention
            || target.ReturnType.FullName != template.ReturnType.FullName
            || target.Parameters.Count != template.Parameters.Count)
            throw new WeavingException($"Handler {target.Name} does not match the dispatch signature of {template.FullName}.");
        for (int i = 0; i < target.Parameters.Count; i++)
            if (target.Parameters[i].ParameterType.FullName != template.Parameters[i].ParameterType.FullName)
                throw new WeavingException($"Handler {target.Name} does not match parameter {i} of {template.FullName}.");
        MethodReference reference = new(target.Name, original.ElementMethod.ReturnType, original.DeclaringType)
        {
            HasThis = original.HasThis,
            ExplicitThis = original.ExplicitThis,
            CallingConvention = original.ElementMethod.CallingConvention
        };
        foreach (GenericParameter parameter in target.GenericParameters)
            reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
        foreach (ParameterDefinition parameter in original.ElementMethod.Parameters)
            reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        GenericInstanceMethod result = new(reference);
        foreach (TypeReference argument in original.GenericArguments) result.GenericArguments.Add(argument);
        return result;
    }
}
