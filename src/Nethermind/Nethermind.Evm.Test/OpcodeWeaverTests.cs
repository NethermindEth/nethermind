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

    [Test]
    public void Opcode_weaver_names_handlers_beside_the_dispatch_template([Values] bool nestedDispatch)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        (MethodDefinition factory, _) = SetUpOpcodeTable(module, ["BadInstructionOpcode"], nestedDispatch);
        module.Types.Add(new TypeDefinition("Nethermind.Evm", "Instruction", TypeAttributes.Class, module.TypeSystem.Object));
        TypeDefinition vm = module.GetType("Nethermind.Evm.VirtualMachine`1");
        TypeDefinition dispatch = nestedDispatch ? vm.NestedTypes[0] : vm;
        ModuleWeaver weaver = new() { ModuleDefinition = module };

        weaver.Execute();

        Assert.That(dispatch.Methods, Has.Some.Matches<MethodDefinition>(method => method.Name == "OpBadInstruction"));
    }

    /// <remarks>A build may install its own dispatch handlers over named ones, but only exact code, never a template.</remarks>
    [Test]
    public void Opcode_weaver_accepts_build_handlers_that_are_exact_code([Values("Plain", "ValueTypeGeneric", "Generic", "Template")] string entry)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        (_, MethodDefinition table) = SetUpOpcodeTable(module, ["BadInstructionOpcode"], nestedDispatch: true);
        module.Types.Add(new TypeDefinition("Nethermind.Evm", "Instruction", TypeAttributes.Class, module.TypeSystem.Object));
        TypeDefinition dispatch = module.GetType("Nethermind.Evm.VirtualMachine`1").NestedTypes[0];
        MethodReference target = dispatch.Methods[0];
        if (entry != "Template")
        {
            MethodDefinition handler = new("ExecuteBuildHandler", MethodAttributes.Static, module.TypeSystem.Void);
            handler.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ret));
            dispatch.Methods.Add(handler);
            target = handler;
            if (entry != "Plain")
            {
                GenericParameter parameter = new("T", handler);
                if (entry == "ValueTypeGeneric") parameter.Attributes = GenericParameterAttributes.NotNullableValueTypeConstraint;
                handler.GenericParameters.Add(parameter);
                GenericInstanceMethod instantiation = new(handler);
                instantiation.GenericArguments.Add(module.TypeSystem.Int32);
                target = instantiation;
            }
        }
        table.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ldftn, target));
        table.Body.Instructions.Add(CilInstruction.Create(OpCodes.Pop));
        ModuleWeaver weaver = new() { ModuleDefinition = module };

        if (entry is "Plain" or "ValueTypeGeneric")
            Assert.That(weaver.Execute, Throws.Nothing);
        else
            Assert.That(weaver.Execute, Throws.TypeOf<WeavingException>().With.Message.Contains("not a named opcode handler"));
    }

    [Test]
    public void Guest_dispatch_marker_becomes_a_direct_tail_call([Values("Direct", "Branch", "Debug")] string epilogue)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        (MethodDefinition marker, MethodDefinition caller, CilInstruction call) = SetUpGuestDispatch(module);
        ILProcessor il = caller.Body.GetILProcessor();
        if (epilogue == "Branch") il.InsertAfter(call, il.Create(OpCodes.Br_S, call.Next));
        if (epilogue == "Debug")
        {
            VariableDefinition result = new(module.TypeSystem.Int32);
            caller.Body.Variables.Add(result);
            CilInstruction load = il.Create(OpCodes.Ldloc_0);
            il.InsertBefore(call.Next, load);
            il.InsertAfter(call, il.Create(OpCodes.Br, load));
            il.InsertAfter(call, il.Create(OpCodes.Stloc_0));
        }
        CilInstruction branch = il.Create(OpCodes.Br, caller.Body.Instructions[0]);
        il.InsertBefore(caller.Body.Instructions[0], branch);

        GuestDispatchRewriter.Rewrite(marker.DeclaringType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(call.OpCode, Is.EqualTo(OpCodes.Tail));
            Assert.That(call.Next.OpCode, Is.EqualTo(OpCodes.Calli));
            Assert.That(call.Next.Next.OpCode, Is.EqualTo(OpCodes.Ret));
            CallSite signature = (CallSite)call.Next.Operand;
            Assert.That(signature.CallingConvention, Is.EqualTo(MethodCallingConvention.Default));
            Assert.That(signature.HasThis, Is.False);
            Assert.That(signature.ReturnType.FullName, Is.EqualTo(caller.ReturnType.FullName));
            Assert.That(signature.Parameters.Count, Is.EqualTo(caller.Parameters.Count));
            for (int i = 0; i < caller.Parameters.Count; i++)
                Assert.That(signature.Parameters[i].ParameterType.FullName, Is.EqualTo(caller.Parameters[i].ParameterType.FullName));
            Assert.That(caller.DeclaringType.Methods, Does.Not.Contain(marker));
        }
    }

    [Test]
    public void Guest_dispatch_rejects_unsupported_marker_uses(
        [Values("Signature", "Target", "Generic", "Instance", "Pointer", "Result", "WrongLocal", "Exception", "Outside", "Unused")] string invalid)
    {
        using ModuleDefinition module = ModuleDefinition.CreateModule("Test", ModuleKind.Dll);
        (MethodDefinition marker, MethodDefinition caller, CilInstruction call) = SetUpGuestDispatch(module);
        TypeDefinition dispatch = marker.DeclaringType;
        ILProcessor il = caller.Body.GetILProcessor();
        switch (invalid)
        {
            case "Signature": marker.Parameters[0].ParameterType = module.TypeSystem.Int32; break;
            case "Target": marker.Parameters[8].ParameterType = module.TypeSystem.Int64; break;
            case "Generic": marker.GenericParameters.Add(new GenericParameter("T", marker)); break;
            case "Instance": marker.HasThis = true; break;
            case "Pointer": call.OpCode = OpCodes.Ldftn; break;
            case "Result": il.InsertAfter(call, il.Create(OpCodes.Pop)); break;
            case "WrongLocal":
                caller.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                caller.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));
                il.InsertAfter(call, il.Create(OpCodes.Ldloc_1));
                il.InsertAfter(call, il.Create(OpCodes.Stloc_0));
                break;
            case "Exception": caller.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)); break;
            case "Outside":
                dispatch.Methods.Remove(caller);
                TypeDefinition other = new("Test", "Other", TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(other);
                other.Methods.Add(caller);
                break;
            case "Unused": il.Remove(call); break;
        }

        Assert.That(() => GuestDispatchRewriter.Rewrite(dispatch), Throws.TypeOf<WeavingException>());
    }

    private static (MethodDefinition Marker, MethodDefinition Caller, CilInstruction Call) SetUpGuestDispatch(ModuleDefinition module)
    {
        MethodDefinition template = CreateWeaverMethod(module, "ExecuteOpcode");
        template.ReturnType = module.TypeSystem.Int32;
        TypeReference[] arguments = [
            new ByReferenceType(module.TypeSystem.Object), module.TypeSystem.UInt64,
            new ByReferenceType(module.TypeSystem.Object), module.TypeSystem.IntPtr, module.TypeSystem.IntPtr,
            new PointerType(module.TypeSystem.IntPtr), new ByReferenceType(module.TypeSystem.Byte), module.TypeSystem.IntPtr];
        MethodDefinition marker = new("TailDispatch", MethodAttributes.Private | MethodAttributes.Static, template.ReturnType);
        MethodDefinition caller = new("ExecuteBuildHandler", MethodAttributes.Static, template.ReturnType);
        template.DeclaringType.Methods.Add(marker);
        template.DeclaringType.Methods.Add(caller);
        foreach (TypeReference argument in arguments)
        {
            template.Parameters.Add(new ParameterDefinition(argument));
            marker.Parameters.Add(new ParameterDefinition(argument));
            ParameterDefinition parameter = new(argument);
            caller.Parameters.Add(parameter);
            caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ldarg, parameter));
        }
        marker.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
        caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ldc_I4_0));
        caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Conv_I));
        CilInstruction call = CilInstruction.Create(OpCodes.Call, marker);
        caller.Body.Instructions.Add(call);
        caller.Body.Instructions.Add(CilInstruction.Create(OpCodes.Ret));
        return (marker, caller, call);
    }
    private static (MethodDefinition Factory, MethodDefinition Table) SetUpOpcodeTable(ModuleDefinition module, string[] opcodeNames, bool nestedDispatch = false)
    {
        MethodDefinition handler = CreateWeaverMethod(module, "ExecuteOpcode");
        TypeDefinition vm = handler.DeclaringType;
        if (nestedDispatch)
        {
            TypeDefinition dispatch = new("", "RawCalliHelper", TypeAttributes.NestedPrivate | TypeAttributes.Class, module.TypeSystem.Object);
            vm.NestedTypes.Add(dispatch);
            vm.Methods.Remove(handler);
            dispatch.Methods.Add(handler);
        }
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
