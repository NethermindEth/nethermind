// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Nethermind.Evm.Fody;

internal sealed class MethodCloner(MethodDefinition source, Action<string> warn)
{
    private MethodDefinition _target = null!;

    public MethodDefinition Clone(string name)
    {
        if (!source.IsStatic)
            throw new WeavingException($"Only static methods can be cloned: {source.FullName}.");
        _target = new MethodDefinition(name, source.Attributes, source.ReturnType)
        {
            DeclaringType = source.DeclaringType,
            ImplAttributes = source.ImplAttributes,
            CallingConvention = source.CallingConvention
        };
        foreach (GenericParameter parameter in source.GenericParameters)
            _target.GenericParameters.Add(new GenericParameter(parameter.Name, _target) { Attributes = parameter.Attributes });
        for (int i = 0; i < source.GenericParameters.Count; i++)
            foreach (GenericParameterConstraint constraint in source.GenericParameters[i].Constraints)
                _target.GenericParameters[i].Constraints.Add(new GenericParameterConstraint(MapType(constraint.ConstraintType)));
        _target.ReturnType = MapType(source.ReturnType);
        foreach (ParameterDefinition parameter in source.Parameters)
            _target.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, MapType(parameter.ParameterType)));
        foreach (CustomAttribute attribute in source.CustomAttributes)
            _target.CustomAttributes.Add(CloneAttribute(attribute));

        MethodBody body = _target.Body;
        body.InitLocals = source.Body.InitLocals;
        body.MaxStackSize = source.Body.MaxStackSize;
        foreach (VariableDefinition variable in source.Body.Variables)
            body.Variables.Add(new VariableDefinition(MapType(variable.VariableType)));

        Dictionary<Instruction, Instruction> instructions = [];
        foreach (Instruction instruction in source.Body.Instructions)
        {
            Instruction copy = Instruction.Create(OpCodes.Nop);
            copy.OpCode = instruction.OpCode;
            instructions.Add(instruction, copy);
            body.Instructions.Add(copy);
        }
        foreach (Instruction instruction in source.Body.Instructions)
        {
            instructions[instruction].Operand = instruction.Operand switch
            {
                Instruction branch => instructions[branch],
                Instruction[] branches => MapBranches(branches, instructions),
                VariableDefinition variable => body.Variables[variable.Index],
                ParameterDefinition parameter => _target.Parameters[parameter.Index],
                TypeReference type => MapType(type),
                MethodReference method => MapMethod(method),
                FieldReference field => new FieldReference(field.Name, MapType(field.FieldType), MapType(field.DeclaringType)),
                CallSite site => MapCallSite(site),
                object operand => operand,
                null => null
            };
        }
        foreach (ExceptionHandler handler in source.Body.ExceptionHandlers)
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = handler.CatchType is null ? null : MapType(handler.CatchType),
                TryStart = instructions[handler.TryStart],
                TryEnd = handler.TryEnd is null ? null : instructions[handler.TryEnd],
                HandlerStart = instructions[handler.HandlerStart],
                HandlerEnd = handler.HandlerEnd is null ? null : instructions[handler.HandlerEnd],
                FilterStart = handler.FilterStart is null ? null : instructions[handler.FilterStart]
            });
        // Cecil exposes sequence points and scope bounds by offset only, and instructions an earlier weaver
        // inserted still carry offset zero. Writing the recomputed offsets back to the source makes them a
        // truthful join key; the writer recomputes them again anyway.
        int offset = 0;
        Dictionary<int, Instruction> byOffset = [];
        foreach (Instruction instruction in source.Body.Instructions)
        {
            instruction.Offset = offset;
            byOffset.Add(offset, instructions[instruction]);
            offset += instruction.GetSize();
        }
        foreach (SequencePoint point in source.DebugInformation.SequencePoints)
        {
            if (!byOffset.TryGetValue(point.Offset, out Instruction? instruction))
            {
                // Debug info only: a stale point drifts a line number, so it is not worth failing the build.
                warn($"Sequence point at IL_{point.Offset:x4} in {source.FullName} is not an instruction.");
                continue;
            }
            _target.DebugInformation.SequencePoints.Add(new SequencePoint(instruction, point.Document)
            {
                StartLine = point.StartLine,
                StartColumn = point.StartColumn,
                EndLine = point.EndLine,
                EndColumn = point.EndColumn
            });
        }
        if (source.DebugInformation.Scope is not null)
            _target.DebugInformation.Scope = CloneScope(source.DebugInformation.Scope, byOffset, body);
        return _target;
    }

    private ScopeDebugInformation? CloneScope(ScopeDebugInformation scope, Dictionary<int, Instruction> byOffset, MethodBody body)
    {
        if (!TryMapOffset(scope.Start, out Instruction? start) || !TryMapOffset(scope.End, out Instruction? end))
            return null;
        ScopeDebugInformation copy = new(start, end) { Import = scope.Import };
        foreach (VariableDebugInformation variable in scope.Variables)
        {
            if ((uint)variable.Index >= (uint)body.Variables.Count)
            {
                warn($"Debug variable {variable.Name} at index {variable.Index} in {source.FullName} has no local.");
                continue;
            }
            copy.Variables.Add(new VariableDebugInformation(body.Variables[variable.Index], variable.Name) { Attributes = variable.Attributes });
        }
        foreach (ConstantDebugInformation constant in scope.Constants)
            copy.Constants.Add(new ConstantDebugInformation(constant.Name, MapType(constant.ConstantType), constant.Value));
        foreach (ScopeDebugInformation nested in scope.Scopes)
            if (CloneScope(nested, byOffset, body) is { } cloned) copy.Scopes.Add(cloned);
        return copy;

        // A scope end at the method's end has no instruction; Cecil represents it as an unresolved offset.
        bool TryMapOffset(InstructionOffset offset, out Instruction? instruction)
        {
            instruction = null;
            if (offset.IsEndOfMethod || byOffset.TryGetValue(offset.Offset, out instruction)) return true;
            warn($"Scope boundary at IL_{offset.Offset:x4} in {source.FullName} is not an instruction; dropping scope and its nested scopes.");
            return false;
        }
    }

    /// <summary>Copies an attribute read from the image by blob; an attribute another weaver built in memory has no blob, so copy its arguments instead.</summary>
    private static CustomAttribute CloneAttribute(CustomAttribute attribute)
    {
        if (!attribute.IsResolved) return new CustomAttribute(attribute.Constructor, attribute.GetBlob());
        CustomAttribute copy = new(attribute.Constructor);
        foreach (CustomAttributeArgument argument in attribute.ConstructorArguments) copy.ConstructorArguments.Add(argument);
        foreach (CustomAttributeNamedArgument field in attribute.Fields) copy.Fields.Add(field);
        foreach (CustomAttributeNamedArgument property in attribute.Properties) copy.Properties.Add(property);
        return copy;
    }

    private static Instruction[] MapBranches(Instruction[] branches, Dictionary<Instruction, Instruction> instructions)
    {
        Instruction[] result = new Instruction[branches.Length];
        for (int i = 0; i < result.Length; i++) result[i] = instructions[branches[i]];
        return result;
    }

    private TypeReference MapType(TypeReference type)
    {
        if (type is GenericParameter parameter && parameter.Owner == source)
            return _target.GenericParameters[parameter.Position];
        if (type is GenericInstanceType instance)
        {
            GenericInstanceType result = new(MapType(instance.ElementType));
            foreach (TypeReference argument in instance.GenericArguments) result.GenericArguments.Add(MapType(argument));
            return result;
        }
        return type switch
        {
            ByReferenceType reference => new ByReferenceType(MapType(reference.ElementType)),
            PointerType pointer => new PointerType(MapType(pointer.ElementType)),
            PinnedType pinned => new PinnedType(MapType(pinned.ElementType)),
            ArrayType array => new ArrayType(MapType(array.ElementType), array.Rank),
            RequiredModifierType modifier => new RequiredModifierType(MapType(modifier.ModifierType), MapType(modifier.ElementType)),
            OptionalModifierType modifier => new OptionalModifierType(MapType(modifier.ModifierType), MapType(modifier.ElementType)),
            FunctionPointerType pointer => MapFunctionPointer(pointer),
            _ => type
        };
    }

    private FunctionPointerType MapFunctionPointer(FunctionPointerType pointer)
    {
        FunctionPointerType result = new()
        {
            ReturnType = MapType(pointer.ReturnType),
            CallingConvention = pointer.CallingConvention,
            HasThis = pointer.HasThis,
            ExplicitThis = pointer.ExplicitThis
        };
        foreach (ParameterDefinition parameter in pointer.Parameters)
            result.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
        return result;
    }

    private MethodReference MapMethod(MethodReference method)
    {
        if (method is GenericInstanceMethod instance)
        {
            GenericInstanceMethod result = new(MapMethod(instance.ElementMethod));
            foreach (TypeReference argument in instance.GenericArguments) result.GenericArguments.Add(MapType(argument));
            return result;
        }
        MethodReference reference = new(method.Name, MapType(method.ReturnType), MapType(method.DeclaringType))
        {
            HasThis = method.HasThis,
            ExplicitThis = method.ExplicitThis,
            CallingConvention = method.CallingConvention
        };
        foreach (GenericParameter parameter in method.GenericParameters)
            reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
        foreach (ParameterDefinition parameter in method.Parameters)
            reference.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
        return reference;
    }

    private CallSite MapCallSite(CallSite site)
    {
        CallSite result = new(MapType(site.ReturnType))
        {
            HasThis = site.HasThis,
            ExplicitThis = site.ExplicitThis,
            CallingConvention = site.CallingConvention
        };
        foreach (ParameterDefinition parameter in site.Parameters)
            result.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
        return result;
    }
}
