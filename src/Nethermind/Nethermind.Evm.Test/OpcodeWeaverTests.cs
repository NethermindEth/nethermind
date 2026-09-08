// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Nethermind.Evm.Fody;
using NUnit.Framework;
using CilInstruction = Mono.Cecil.Cil.Instruction;

namespace Nethermind.Evm.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class OpcodeWeaverTests
{
    [Test]
    public void Opcode_cloner_rejects_instance_methods()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        MethodDefinition method = CreateWeaverMethod(module, "Instance");
        method.IsStatic = false;
        method.HasThis = true;
        method.Body.Instructions.Insert(0, CilInstruction.Create(OpCodes.Ldarg, method.Body.ThisParameter));

        Assert.That(() => new MethodCloner(method, _ => { }).Clone("Clone"),
            Throws.TypeOf<WeavingException>().With.Message.Contains("Only static methods"));
    }

    [Test]
    public void Opcode_cloner_drops_stale_scopes([Values] bool nested, [Values] bool staleStart)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        MethodDefinition method = CreateWeaverMethod(module, "Template");
        CilInstruction first = method.Body.Instructions[0];
        ScopeDebugInformation scope = new(first, null);
        if (staleStart) scope.Start = new InstructionOffset(99);
        else scope.End = new InstructionOffset(99);
        scope.Scopes.Add(new ScopeDebugInformation(first, null));
        method.DebugInformation.SequencePoints.Add(new SequencePoint(first, new Document("Test.cs")) { StartLine = 1, EndLine = 1 });
        method.DebugInformation.Scope = nested ? new ScopeDebugInformation(first, null) : scope;
        if (nested) method.DebugInformation.Scope.Scopes.Add(scope);
        List<string> warnings = [];

        MethodDefinition clone = new MethodCloner(method, warnings.Add).Clone("Clone");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("dropping scope and its nested scopes"));
            if (nested) Assert.That(clone.DebugInformation.Scope.Scopes, Is.Empty);
            else Assert.That(clone.DebugInformation.Scope, Is.Null);
            Assert.That(clone.Body.Instructions, Has.Count.EqualTo(1));
            Assert.That(clone.DebugInformation.SequencePoints, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Opcode_cloner_skips_stale_debug_locals()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        MethodDefinition method = CreateWeaverMethod(module, "Template");
        method.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        VariableDefinition removed = new(module.TypeSystem.Int32);
        method.Body.Variables.Add(removed);
        method.DebugInformation.Scope = new ScopeDebugInformation(method.Body.Instructions[0], null);
        method.DebugInformation.Scope.Variables.Add(new VariableDebugInformation(method.Body.Variables[0], "kept"));
        method.DebugInformation.Scope.Variables.Add(new VariableDebugInformation(removed, "removed"));
        MethodBody updatedBody = new(method);
        updatedBody.Instructions.Add(method.Body.Instructions[0]);
        updatedBody.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
        method.Body = updatedBody;
        List<string> warnings = [];

        MethodDefinition clone = new MethodCloner(method, warnings.Add).Clone("Clone");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warnings, Is.EqualTo(new[] { $"Debug variable removed at index 1 in {method.FullName} has no local." }));
            Assert.That(clone.DebugInformation.Scope.Variables, Has.Count.EqualTo(1));
            Assert.That(clone.DebugInformation.Scope.Variables[0].Name, Is.EqualTo("kept"));
        }
    }

    [Test]
    public void Opcode_retarget_rejects_incompatible_signatures([Values("count", "type", "return", "instance")] string mismatch)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        MethodDefinition method = CreateWeaverMethod(module, "Template");
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        MethodDefinition clone = new MethodCloner(method, _ => { }).Clone("Clone");
        switch (mismatch)
        {
            case "count": clone.Parameters.Clear(); break;
            case "type": clone.Parameters[0].ParameterType = module.TypeSystem.Int64; break;
            case "return": clone.ReturnType = module.TypeSystem.Int32; break;
            case "instance": clone.HasThis = true; break;
        }

        Assert.That(() => ModuleWeaver.Retarget(new GenericInstanceMethod(method), clone),
            Throws.TypeOf<WeavingException>());
    }

    [Test]
    public void Opcode_weaver_rejects_names_differing_only_by_case()
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        SetUpOpcodeTable(module, ["SLoadOpcode", "SloadOpcode"]);
        ModuleWeaver weaver = new() { ModuleDefinition = module };

        Assert.That(weaver.Execute, Throws.TypeOf<WeavingException>().With.Message.Contains("differ only by case"));
    }

    [Test]
    public void Opcode_validation_ignores_unrelated_unresolved_calls_but_rejects_factory_calls([Values] bool factoryCall, [Values] bool inTable)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        (MethodDefinition factory, MethodDefinition table) = SetUpOpcodeTable(module, ["BadInstructionOpcode"]);
        module.Types.Add(new TypeDefinition("Nethermind.Evm", "Instruction", TypeAttributes.Class, module.TypeSystem.Object));
        TypeDefinition unrelated = new("Test", "Unrelated", TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(unrelated);
        MethodDefinition caller = new("Caller", MethodAttributes.Static, module.TypeSystem.Void);
        unrelated.Methods.Add(caller);
        AssemblyNameReference missing = new("MissingAssembly", new Version(1, 0));
        TypeReference external = new("Missing", "External", module, missing);
        MethodDefinition externalCaller = inTable ? table : caller;
        externalCaller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Call, new MethodReference("UnrelatedCall", module.TypeSystem.Void, external)));
        TypeDefinition vm = module.GetType("Nethermind.Evm.VirtualMachine`1");
        if (factoryCall)
            caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Call, factory));
        caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ret));
        ModuleWeaver weaver = new() { ModuleDefinition = module };

        if (factoryCall)
            Assert.That(weaver.Execute, Throws.TypeOf<WeavingException>().With.Message.Contains("still references an original opcode factory"));
        else
        {
            weaver.Execute();
            foreach (MethodDefinition method in vm.Methods)
                Assert.That(method.Name, Is.Not.EqualTo("OpcodeHandler"));
        }
    }

    private static (MethodDefinition Factory, MethodDefinition Table) SetUpOpcodeTable(ModuleDefinition module, string[] opcodeNames)
    {
        MethodDefinition handler = CreateWeaverMethod(module, "ExecuteOpcode");
        TypeDefinition vm = handler.DeclaringType;
        handler.Body.Instructions.Insert(0, CilInstruction.Create(OpCodes.Tail));
        MethodDefinition factory = new("OpcodeHandler", MethodAttributes.Static, module.TypeSystem.Void);
        vm.Methods.Add(factory);
        factory.GenericParameters.Add(new GenericParameter("TOpcode", factory));
        factory.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ldftn, new GenericInstanceMethod(handler)));
        factory.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ret));
        MethodDefinition table = new("GenerateOpcodeHandlers", MethodAttributes.Static, module.TypeSystem.Void);
        vm.Methods.Add(table);
        foreach (string name in opcodeNames)
        {
            TypeDefinition opcode = new("Test", name, TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(opcode);
            GenericInstanceMethod call = new(factory);
            call.GenericArguments.Add(opcode);
            table.Body.Instructions.Add(CilInstruction.Create(OpCodes.Call, call));
        }
        return (factory, table);
    }

    private static MethodDefinition CreateWeaverMethod(ModuleDefinition module, string name)
    {
        TypeDefinition vm = new("Nethermind.Evm", "VirtualMachine`1", TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(vm);
        MethodDefinition method = new(name, MethodAttributes.Static, module.TypeSystem.Void);
        vm.Methods.Add(method);
        method.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ret));
        return method;
    }
}
