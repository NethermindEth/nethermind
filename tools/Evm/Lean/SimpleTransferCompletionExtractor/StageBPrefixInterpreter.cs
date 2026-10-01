// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBPrefixInterpreter
{
    internal static StageBPrefixRun Run(StageBPrefixProgram program, StageBPostNonceInput input, StageBResponseTape responses, long fuel)
    {
        try
        {
            Validate(program);
            input.Validate();
            if (fuel <= 0 || fuel > program.FuelBound) throw new StageBExecutionException("fuel", $"expected 1..{program.FuelBound}");
            return new Machine(program, input, responses, fuel).Run();
        }
        catch (FuelSignal)
        {
            return new(StageBRunOutcomeKind.FuelExhausted, null, null, "fuel exhausted", []);
        }
        catch (StageBExecutionException exception)
        {
            return new(StageBRunOutcomeKind.Rejected, null, null, exception.Message, [], fuel, exception.Rejection);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(StageBRunOutcomeKind.Fault, null, null, exception.Message, []);
        }
    }

    private static bool IsSupportedOperation(StageBOperationKind kind) => kind is
        StageBOperationKind.DeclarationExpression or StageBOperationKind.ExpressionStatement or StageBOperationKind.SimpleAssignment or
        StageBOperationKind.Invocation or StageBOperationKind.ObjectCreation or StageBOperationKind.CollectionExpression or
        StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference or StageBOperationKind.LocalReference or
        StageBOperationKind.ParameterReference or StageBOperationKind.InstanceReference or StageBOperationKind.Literal or
        StageBOperationKind.DefaultValue or StageBOperationKind.Binary or StageBOperationKind.Unary or StageBOperationKind.Conversion or
        StageBOperationKind.Parenthesized or StageBOperationKind.Argument or StageBOperationKind.IsPattern or
        StageBOperationKind.ConstantPattern or StageBOperationKind.NegatedPattern or StageBOperationKind.IsNull or
        StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or StageBOperationKind.Discard;

    private static void Validate(StageBPrefixProgram program)
    {
        if (program.Integrity != StageBPrefixIntegrity.Compute(program))
            throw new StageBExecutionException("program-integrity", "compiled prefix changed");
        if (program.AcceptedStages.Select(static stage => stage.Name).Order(StringComparer.Ordinal).ToArray() is not
            ["ordinary-refund", "receipt-terminal", "state-charge", "transaction-initialization"])
            throw new StageBExecutionException("program", "accepted-stage inventory");
        if (program.AcceptedStages.Any(static stage => stage.EntryPoints.Length == 0 || stage.SourceClosure.Length == 0))
            throw new StageBExecutionException("program", "unbound accepted stage");
        if (StageBPrefixIntegrity.StageClosure(program.AcceptedStages.Single(static stage => stage.Name == "transaction-initialization")) !=
                "dc5b939ff49b7e6d470f5fa1fbb925cd254ee38d1481576de46d307f9ee85977" ||
            StageBPrefixIntegrity.StageClosure(program.AcceptedStages.Single(static stage => stage.Name == "state-charge")) !=
                "817e22be41c079470ed9cfa9fdc2b892d754ceb4752b3282ee9b09a78009ac59")
            throw new StageBExecutionException("stage-source", "linked gas kernel closure changed");
        if (!program.Functions.Any(function => function.Signature.Symbol == program.Entry))
            throw new StageBExecutionException("program", "entry function");
        foreach (StageBPrefixFunction function in program.Functions)
        {
            if (function.Blocks.Select(static block => block.Ordinal).Distinct().Count() != function.Blocks.Length)
                throw new StageBExecutionException("program", $"duplicate block in {function.Signature.Name}");
            foreach (StageBPrefixBlock block in function.Blocks)
            {
                foreach (StageBPrefixNode node in block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue])
                    ValidateNode(node);
            }
        }

        static void ValidateNode(StageBPrefixNode node)
        {
            if (!IsSupportedOperation(node.Kind)) throw new StageBExecutionException("unsupported-operation", node.Kind.ToString());
            if (node.AdditionalTargets.Any(static target => target is not
                { Kind: StageBPrefixTargetKind.ExternalRequest, RequestKind: StageBRequestKind.Collection, Member.Name: "Add" }))
                throw new StageBExecutionException("unsupported-additional-target", node.Kind.ToString());
            foreach (StageBPrefixNode child in node.Children) ValidateNode(child);
        }
    }

    private sealed class Machine
    {
        private readonly StageBPrefixProgram _program;
        private readonly StageBPostNonceInput _input;
        private readonly StageBResponseTape _responses;
        private readonly Dictionary<string, StageBPrefixFunction> _functions;
        private readonly Dictionary<string, Cell> _statics = new(StringComparer.Ordinal);
        private readonly List<StageBRequest> _requests = [];
        private long _fuel;

        internal Machine(StageBPrefixProgram program, StageBPostNonceInput input, StageBResponseTape responses, long fuel)
        {
            _program = program;
            _input = input;
            _responses = responses;
            _fuel = fuel;
            _functions = program.Functions.ToDictionary(static function => function.Signature.Symbol, StringComparer.Ordinal);
        }

        internal StageBPrefixRun Run()
        {
            Frame root = RootFrame(_functions[_program.Entry]);
            try
            {
                StageBValue value = Execute(root);
                return new(StageBRunOutcomeKind.Returned, null, value, "", _requests.ToArray(), _fuel);
            }
            catch (SuspendSignal signal)
            {
                StageBValue gas = signal.Operands.Single(static operand => operand.Ordinal == 5).Value;
                long reservoir = Signed(signal.Operands.Single(static operand => operand.Ordinal == 10).Value);
                StageBRefundSuspension suspension = new(signal.Operands, Gas(gas), reservoir,
                    signal.Operands.Single(static operand => operand.Ordinal == 4).Value, _requests.ToArray(), _fuel);
                return new(StageBRunOutcomeKind.RefundSuspended, suspension, null, "", _requests.ToArray(), _fuel);
            }
            catch (OutsideSignal)
            {
                return new(StageBRunOutcomeKind.OutsideSelectedDomain, null, null, "selected CFG edge left the admitted prefix", _requests.ToArray(), _fuel);
            }
            catch (FuelSignal)
            {
                return new(StageBRunOutcomeKind.FuelExhausted, null, null, "fuel exhausted", _requests.ToArray(), _fuel);
            }
            catch (StageBExecutionException exception)
            {
                return new(StageBRunOutcomeKind.Rejected, null, null, exception.Message, _requests.ToArray(), _fuel, exception.Rejection);
            }
        }

        private Frame RootFrame(StageBPrefixFunction function)
        {
            Frame frame = new(function, Operand.Value(StageBValue.Reference(function.Signature.DeclaringType, "processor")));
            foreach (StageBParameter parameter in function.Signature.Parameters)
            {
                StageBValue value = parameter.Name switch
                {
                    "tx" => StageBValue.Reference(parameter.Type, "tx"),
                    "tracer" => StageBValue.Reference(parameter.Type, "tracer"),
                    "opts" => StageBValue.Reference(parameter.Type, "opts"),
                    "header" => StageBValue.Reference(parameter.Type, "header"),
                    "spec" => StageBValue.Reference(parameter.Type, "spec"),
                    "intrinsicGas" => _input.IntrinsicGas.ToValue(),
                    _ => throw new StageBExecutionException("entry-parameter", parameter.Name),
                };
                frame.Cells[parameter.Symbol] = new(parameter.Symbol, value);
            }

            foreach (StageBBinding binding in function.Bindings)
            {
                string name = LocalName(binding.Symbol);
                StageBValue? value = name switch
                {
                    "restore" => StageBValue.Bool(_input.Restore),
                    "commit" => StageBValue.Bool(_input.Commit),
                    "deleteCallerAccount" => StageBValue.Bool(_input.DeleteCallerAccount),
                    "opcodeGasPrice" => StageBValue.UInt256(_input.OpcodeGasPrice),
                    "premiumPerGas" => StageBValue.UInt256(_input.PremiumPerGas),
                    "senderReservedGasPayment" => StageBValue.UInt256(_input.SenderReservedGasPayment),
                    "blobBaseFee" => StageBValue.UInt256(_input.BlobBaseFee),
                    _ => null,
                };
                if (value is not null) frame.Cells[binding.Symbol] = new(binding.Symbol, value);
            }
            return frame;
        }

        private StageBValue Execute(Frame frame)
        {
            Dictionary<int, StageBPrefixBlock> blocks = frame.Function.Blocks.ToDictionary(static block => block.Ordinal);
            StageBBlockCursor<StageBValue> cursor = new(frame.Function.Entry.Block, StageBValue.Unit);
            while (true)
            {
                Tick();
                if (!blocks.TryGetValue(cursor.Ordinal, out StageBPrefixBlock? block)) throw new StageBExecutionException("cfg-block", cursor.Ordinal.ToString(CultureInfo.InvariantCulture));
                if (block.Exit == StageBPrefixExit.OutsideSelectedDomain) throw new OutsideSignal();
                StageBValue last = cursor.Carried;
                foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();
                if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();
                StageBFinishResult<StageBValue> result = StageBControlKernel.FinishBlock(block.Exit, new StageBBlockCursor<StageBValue>(cursor.Ordinal, last),
                    block.ConditionKind, last.Kind == StageBValueKind.Bool, last.Boolean,
                    block.FallThrough is not null, block.FallThrough?.Destination ?? -1, block.FallThrough?.Semantics == "Return",
                    block.Conditional is not null, block.Conditional?.Destination ?? -1, block.Conditional?.Semantics == "Return");
                if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;
                if (result.Kind == StageBEdgeKind.MissingSuspension) throw new StageBExecutionException("missing-suspension", frame.Function.Signature.Name);
                if (result.Kind == StageBEdgeKind.InvalidCondition)
                    throw new StageBExecutionException("cfg-condition", $"{frame.Function.Signature.Name}:{cursor.Ordinal}:Stage-B execution bool: {last.Type}.");
                if (result.Kind != StageBEdgeKind.Jump) throw new StageBExecutionException("cfg-edge", frame.Function.Signature.Name);
                cursor = result.Cursor;
            }
        }

        private Operand Evaluate(Frame frame, StageBPrefixNode node)
        {
            Tick();
            return node.Kind switch
            {
                StageBOperationKind.ExpressionStatement or StageBOperationKind.Parenthesized or StageBOperationKind.Argument or
                    StageBOperationKind.DeclarationExpression or StageBOperationKind.Conversion => EvaluateTransparent(frame, node),
                StageBOperationKind.SimpleAssignment => Assign(frame, node),
                StageBOperationKind.Invocation => Invoke(frame, node, constructing: false),
                StageBOperationKind.ObjectCreation => Invoke(frame, node, constructing: true),
                StageBOperationKind.CollectionExpression => Collection(frame, node),
                StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference => Member(frame, node),
                StageBOperationKind.LocalReference or StageBOperationKind.ParameterReference => Binding(frame, node),
                StageBOperationKind.InstanceReference => Instance(frame, node),
                StageBOperationKind.Literal or StageBOperationKind.DefaultValue => Operand.Value(Constant(node.Type, node.Constant)),
                StageBOperationKind.Binary => Binary(frame, node),
                StageBOperationKind.Unary => Unary(frame, node),
                StageBOperationKind.IsPattern => IsPattern(frame, node),
                StageBOperationKind.ConstantPattern => Operand.Value(Evaluate(frame, node.Children[0]).Read()),
                StageBOperationKind.NegatedPattern => Operand.Value(StageBValue.Bool(!Boolean(Evaluate(frame, node.Children[0]).Read()))),
                StageBOperationKind.IsNull => Operand.Value(StageBValue.Bool(Evaluate(frame, node.Children[0]).Read().Kind == StageBValueKind.Null)),
                StageBOperationKind.FlowCapture => Capture(frame, node),
                StageBOperationKind.FlowCaptureReference => frame.Captures.TryGetValue(node.Binding.Capture, out Operand captured)
                    ? captured : throw new StageBExecutionException("capture-read", node.Binding.Capture.ToString(CultureInfo.InvariantCulture)),
                StageBOperationKind.Discard => Operand.Location(new DiscardLocation(node.Type)),
                _ => throw new StageBExecutionException("unsupported-operation", node.Kind.ToString()),
            };
        }

        private Operand EvaluateTransparent(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 1) throw new StageBExecutionException("transparent-arity", node.Kind.ToString());
            Operand operand = Evaluate(frame, node.Children[0]);
            if (node.Kind == StageBOperationKind.Conversion && node.Call is { } call)
                return Call(frame, node, call, [operand], constructing: false);
            if (node.Kind == StageBOperationKind.Conversion && node.Mode == StageBPrefixOperandMode.Value)
                return Operand.Value(ConvertValue(operand.Read(), node.Type));
            bool preservesOperand = node.Kind is StageBOperationKind.Argument or StageBOperationKind.DeclarationExpression;
            return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)
                ? Operand.Value(operand.Read()) : operand;
        }

        private Operand Assign(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 2) throw new StageBExecutionException("assignment-arity", node.Symbol);
            Operand target = Evaluate(frame, node.Children[0]);
            Operand source = Evaluate(frame, node.Children[1]);
            if (node.Binding.IsRef)
            {
                if (target.Target is not CellLocation cell || source.Target is null) throw new StageBExecutionException("ref-assignment", node.Symbol);
                cell.Cell.Alias = source.Target;
                return source;
            }
            try { target.Write(source.Read()); }
            catch (StageBExecutionException exception) { throw new StageBExecutionException("assignment", $"{node.Children[0].Kind}/{node.Children[0].Symbol}:{exception.Message}"); }
            return Operand.Value(source.Read());
        }

        private Operand Invoke(Frame frame, StageBPrefixNode node, bool constructing)
        {
            if (node.Call is null) throw new StageBExecutionException("missing-call", node.Symbol);
            Operand[] operands = new Operand[node.Children.Length];
            for (int index = 0; index < node.Children.Length; index++) operands[index] = Evaluate(frame, node.Children[index]);
            return Call(frame, node, node.Call, operands, constructing);
        }

        private Operand Call(Frame caller, StageBPrefixNode node, StageBPrefixCall call, Operand[] operands, bool constructing)
        {
            if (call.Target.Kind == StageBPrefixTargetKind.RefundSuspension)
            {
                StageBRefundOperand[] refund = call.Arguments.OrderBy(static argument => argument.Ordinal).Select(argument =>
                {
                    Operand operand = operands[argument.Child];
                    return new StageBRefundOperand(argument.Ordinal, argument.Mode, operand.Read(), operand.Target?.Identity ?? "",
                        operand.Target?.Resolve());
                }).ToArray();
                throw new SuspendSignal(refund);
            }
            if (call.Target.Kind == StageBPrefixTargetKind.Initialization) return Operand.Value(Initialize(operands, call));
            if (call.Target.Kind == StageBPrefixTargetKind.StateCharge) return Operand.Value(Charge(operands, call));
            if (call.Target.Kind == StageBPrefixTargetKind.StaticField)
            {
                if (!_statics.TryGetValue(call.Target.Member.Symbol, out Cell? cell))
                {
                    StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));
                    cell = new("static:" + call.Target.Member.Symbol, value);
                    _statics.Add(call.Target.Member.Symbol, cell);
                }
                Operand location = Operand.Location(new CellLocation(cell, readOnly: true));
                return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(location.Read()) : location;
            }
            if (call.Target.Kind == StageBPrefixTargetKind.DefaultValue)
            {
                StageBValue value = Default(call.Target.Member.Type);
                return constructing && node.Mode != StageBPrefixOperandMode.Value
                    ? Operand.Location(new CellLocation(new Cell("new:" + call.Target.Member.Symbol, value), readOnly: false))
                    : Operand.Value(value);
            }
            if (call.Target.Kind == StageBPrefixTargetKind.ExternalRequest) return External(caller, node, call, operands);
            if (call.Target.Kind != StageBPrefixTargetKind.Local || !_functions.TryGetValue(call.Target.Body, out StageBPrefixFunction? function))
                throw new StageBExecutionException("call-target", call.Target.Member.Name);

            Operand receiver;
            if (constructing)
            {
                Cell receiverCell = new("new:" + call.Target.Member.Symbol, Default(call.Target.Member.DeclaringType));
                receiver = Operand.Location(new CellLocation(receiverCell, readOnly: false));
            }
            else
            {
                receiver = call.ReceiverChild >= 0 ? operands[call.ReceiverChild] : Operand.Value(StageBValue.Unit);
            }
            Frame frame = new(function, receiver);
            if (call.Arguments.Length == 0 && function.Signature.Parameters.Length != 0)
            {
                if (operands.Length != function.Signature.Parameters.Length) throw new StageBExecutionException("operator-arity", call.Target.Member.Name);
                for (int ordinal = 0; ordinal < operands.Length; ordinal++)
                    frame.Cells[function.Signature.Parameters[ordinal].Symbol] = new(function.Signature.Parameters[ordinal].Symbol, operands[ordinal].Read());
            }
            foreach (StageBPrefixArgument argument in call.Arguments)
            {
                StageBParameter parameter = function.Signature.Parameters[argument.Ordinal];
                Operand actual = operands[argument.Child];
                if (parameter.RefKind is StageBRefKind.Ref or StageBRefKind.Out or StageBRefKind.In or StageBRefKind.RefReadOnly or StageBRefKind.RefReadOnlyParameter)
                {
                    Location location = actual.Target ?? (parameter.RefKind is StageBRefKind.In or StageBRefKind.RefReadOnly or StageBRefKind.RefReadOnlyParameter
                        ? new ValueLocation((argument.Mode == StageBArgumentMode.ReadOnlyTemporary ? "temporary:" : "readonly-expression:") + parameter.Symbol, actual.Read())
                        : throw new StageBExecutionException("argument-location", $"{parameter.Name}/{argument.Mode}"));
                    frame.Cells[parameter.Symbol] = new(parameter.Symbol, Default(parameter.Type)) { Alias = location };
                }
                else frame.Cells[parameter.Symbol] = new(parameter.Symbol, actual.Read());
            }
            StageBValue result = Execute(frame);
            StageBLocalReturnKind returnKind = StageBControlKernel.SelectLocalReturn(constructing, node.Mode == StageBPrefixOperandMode.Value);
            if (returnKind == StageBLocalReturnKind.ReturnedValue) return Operand.Value(result);
            return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : receiver;
        }

        private Operand Collection(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 0) throw new StageBExecutionException("unsupported-collection", "nonempty");
            return Operand.Value(StageBValue.Struct(node.Type));
        }

        private Operand Binding(Frame frame, StageBPrefixNode node)
        {
            if (!frame.Cells.TryGetValue(node.Symbol, out Cell? cell))
            {
                if (node.Mode == StageBPrefixOperandMode.Value && !node.Operator.Contains("declaration=True", StringComparison.Ordinal))
                    throw new StageBExecutionException("uninitialized-binding", node.Symbol);
                cell = new(node.Symbol, Default(node.Type));
                frame.Cells.Add(node.Symbol, cell);
            }
            Operand operand = Operand.Location(new CellLocation(cell, node.Mode == StageBPrefixOperandMode.ReadOnlyLocation));
            return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(operand.Read()) : operand;
        }

        private Operand Instance(Frame frame, StageBPrefixNode node)
        {
            if (node.Operator == "ContainingTypeInstance") return node.Mode == StageBPrefixOperandMode.Value
                ? Operand.Value(frame.This.Read()) : frame.This;
            return Binding(frame, node);
        }

        private Operand Member(Frame frame, StageBPrefixNode node)
        {
            if (node.Call is null) throw new StageBExecutionException("member-target", node.Symbol);
            if (node.Call.Target.Kind == StageBPrefixTargetKind.StaticField) return Call(frame, node, node.Call, [], constructing: false);
            Operand receiver = node.Children.Length == 0 ? Operand.Value(StageBValue.Unit) : Evaluate(frame, node.Children[0]);
            if (node.Mode != StageBPrefixOperandMode.Value && receiver.Read().Text == "tx" && node.Call.Target.Member.Name == "ValueRef")
                return Operand.Location(new ValueLocation("tx.ValueRef", StageBValue.UInt256(_input.Transaction.Value)));
            if (node.Mode != StageBPrefixOperandMode.Value && receiver.Target is not null && receiver.Read().Kind == StageBValueKind.Struct)
                return Operand.Location(new FieldLocation(receiver.Target, node.Call.Target.Member.Name, node.Mode == StageBPrefixOperandMode.ReadOnlyLocation));
            if (node.Call.Target.Kind == StageBPrefixTargetKind.Local)
                return Call(frame, node, node.Call, node.Children.Length == 0 ? [] : [receiver], constructing: false);
            return Projection(node.Call.Target.Member, receiver.Read(), node.Constant);
        }

        private Operand Projection(StageBMember member, StageBValue receiver, string constant)
        {
            if (receiver.Kind == StageBValueKind.Struct && receiver.Fields?.ContainsKey(member.Name) == true)
                return Operand.Value(receiver.Field(member.Name));
            if (member.Kind == StageBMemberKind.Field && constant.Length != 0)
                return Operand.Value(Constant(member.Type, constant));
            StageBValue value = (receiver.Text, member.Name) switch
            {
                ("tx", "SenderAddress") => StageBValue.Address(_input.Transaction.Sender),
                ("tx", "To") => _input.Transaction.Recipient is { } recipient ? StageBValue.Address(recipient) : StageBValue.Null(member.Type),
                ("tx", "Value") or ("tx", "ValueRef") => StageBValue.UInt256(_input.Transaction.Value),
                ("tx", "Data") => StageBValue.BytesValue(_input.Transaction.Data),
                ("tx", "GasLimit") => StageBValue.UInt64(_input.Transaction.GasLimit),
                ("tx", "AuthorizationList") => _input.Transaction.HasAuthorizationList
                    ? StageBValue.Reference(member.Type, "authorization-list") : StageBValue.Null(member.Type),
                ("spec", "IsEip8037Enabled") => StageBValue.Bool(_input.Spec.IsEip8037Enabled),
                ("spec", "IsEip7708Enabled") => StageBValue.Bool(_input.Spec.IsEip7708Enabled),
                ("tracer", "IsTracingState") => StageBValue.Bool(_input.Tracer.IsTracingState),
                ("tracer", "IsTracingActions") => StageBValue.Bool(_input.Tracer.IsTracingActions),
                ("tracer", "IsTracingCode") => StageBValue.Bool(_input.Tracer.IsTracingCode),
                ("tracer", "IsTracingLogs") => StageBValue.Bool(_input.Tracer.IsTracingLogs),
                ("tracer", "IsTracingAccess") => StageBValue.Bool(_input.Tracer.IsTracingAccess),
                ("processor", "WorldState") => StageBValue.Reference(member.Type, "world-state"),
                ("processor", "_codeInfoRepository") => StageBValue.Reference(member.Type, "code-repository"),
                ("processor", "_isCodeOverridable") => StageBValue.Bool(_input.IsCodeOverridable),
                ("processor", "Logger") or ("processor", "_logger") => StageBValue.Reference(member.Type, "logger"),
                _ when member.Name == "ForceSimpleTransferDisabled" => StageBValue.Bool(_input.ForceSimpleTransferDisabled),
                _ when member.Name == "DefaultTxGasLimitCap" => StageBValue.UInt64(_input.ExecutionGasLimitCap),
                _ when member.Name == "NewAccountState" => StageBValue.Int64(_input.NewAccountStateCost),
                _ when member.Name == "Zero" => StageBValue.UInt256(BigInteger.Zero),
                _ when member.Name == "Instance" => StageBValue.Reference(member.Type, "null-tracer"),
                _ when member.Name == "IsZero" && receiver.Kind == StageBValueKind.UInt256 => StageBValue.Bool(receiver.Integer.IsZero),
                _ when member.Name == "IsEmpty" && receiver.Type.Contains("CodeInfo", StringComparison.Ordinal) => receiver.Field("IsEmpty"),
                _ when receiver.Kind == StageBValueKind.Struct => receiver.Field(member.Name),
                _ => throw new StageBExecutionException("projection", $"{receiver.Type}.{member.Name}"),
            };
            return Operand.Value(value);
        }

        private Operand External(Frame frame, StageBPrefixNode node, StageBPrefixCall call, Operand[] operands)
        {
            StageBMember member = call.Target.Member;
            StageBValue[] arguments = Arguments(call, operands);
            StageBValue receiver = call.ReceiverChild >= 0 ? operands[call.ReceiverChild].Read() : StageBValue.Unit;
            switch (member.Name)
            {
                case "GetCachedCodeInfo":
                {
                    string recipient = Address(arguments[0]);
                    bool follow = Boolean(arguments[1]);
                    StageBCodeLookupRequest request = new(recipient, follow);
                    StageBCodeLookupExchange reply = Request<StageBCodeLookupExchange>(request);
                    WriteArgument(call, operands, 3, reply.DelegationAddress is null ? StageBValue.Null(member.Parameters[3].Type) : StageBValue.Address(reply.DelegationAddress));
                    return Operand.Value(StageBValue.Struct(member.Type, ("IsEmpty", StageBValue.Bool(reply.IsEmpty))));
                }
                case "IsDeadAccount":
                {
                    StageBIsDeadAccountRequest request = new(Address(arguments[0]));
                    return Operand.Value(StageBValue.Bool(Request<StageBBoolExchange>(request).Value));
                }
                case "SubtractFromBalance":
                {
                    StageBSubtractBalanceRequest request = new(Address(arguments[0]), UInt256(arguments[1]));
                    Request<StageBUnitExchange>(request);
                    return Operand.Value(StageBValue.Unit);
                }
                case "AddToBalanceAndCreateIfNotExists":
                {
                    StageBAddBalanceRequest request = new(Address(arguments[0]), UInt256(arguments[1]));
                    return Operand.Value(StageBValue.Bool(Request<StageBBoolExchange>(request).Value));
                }
                case "Commit":
                {
                    StageBCommitRequest request = new(arguments.Length > 1 && arguments[1].Text == "tracer", Boolean(arguments[^1]));
                    Request<StageBUnitExchange>(request);
                    return Operand.Value(StageBValue.Unit);
                }
                case "IncrementEmptyCalls":
                case "ReportAction":
                case "ReportByteCode":
                case "ReportActionError":
                case "ReportActionEnd":
                case "ReportLog":
                {
                    StageBTraceRequest request = new(member.Name, arguments);
                    Request<StageBUnitExchange>(request);
                    return Operand.Value(StageBValue.Unit);
                }
                case "HasFlag": return Operand.Value(StageBValue.Bool(_input.Warmup && Signed(arguments[0]) == 8));
                case "Min": return Operand.Value(StageBValue.UInt256(BigInteger.Min(UInt256(arguments[0]), UInt256(arguments[1]))));
                case "op_Equality":
                    if (arguments.Any(static argument => argument.Kind is StageBValueKind.Bytes or StageBValueKind.Struct))
                        throw new StageBExecutionException("unsupported-route", "reference identity over quotiented values");
                    return Operand.Value(StageBValue.Bool(ValueEquals(arguments[0], arguments[1])));
                case "ToBigEndian": throw new StageBExecutionException("unsupported-route", "EIP-7708 transfer log");
                case ".ctor" when member.DeclaringType.Contains("Journal", StringComparison.Ordinal):
                    return Operand.Value(StageBValue.Struct(member.DeclaringType));
                default:
                    if (member.Kind is StageBMemberKind.Field or StageBMemberKind.Property) return Projection(member, receiver, node.Constant);
                    throw new StageBExecutionException("external-call", member.Symbol);
            }
        }

        private Operand Binary(Frame frame, StageBPrefixNode node)
        {
            Operand[] operands = node.Children.Select(child => Evaluate(frame, child)).ToArray();
            if (node.Call is { } call) return Call(frame, node, call, operands, constructing: false);
            throw new StageBExecutionException("binary", node.Operator);
        }

        private Operand Unary(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 1 || !node.Operator.StartsWith("Not;", StringComparison.Ordinal))
                throw new StageBExecutionException("unary", node.Operator);
            return Operand.Value(StageBValue.Bool(!Boolean(Evaluate(frame, node.Children[0]).Read())));
        }

        private Operand IsPattern(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 2) throw new StageBExecutionException("pattern-arity", node.Operator);
            StageBValue input = Evaluate(frame, node.Children[0]).Read();
            StageBPrefixNode pattern = node.Children[1];
            bool matches = pattern.Kind switch
            {
                StageBOperationKind.ConstantPattern => ValueEquals(input, Evaluate(frame, pattern.Children[0]).Read()),
                StageBOperationKind.NegatedPattern => !Pattern(frame, input, pattern.Children[0]),
                _ => throw new StageBExecutionException("pattern", pattern.Kind.ToString()),
            };
            return Operand.Value(StageBValue.Bool(matches));
        }

        private bool Pattern(Frame frame, StageBValue input, StageBPrefixNode pattern) => pattern.Kind switch
        {
            StageBOperationKind.ConstantPattern => ValueEquals(input, Evaluate(frame, pattern.Children[0]).Read()),
            StageBOperationKind.NegatedPattern => !Pattern(frame, input, pattern.Children[0]),
            _ => throw new StageBExecutionException("pattern", pattern.Kind.ToString()),
        };

        private Operand Capture(Frame frame, StageBPrefixNode node)
        {
            if (node.Children.Length != 1) throw new StageBExecutionException("capture-arity", node.Binding.Capture.ToString(CultureInfo.InvariantCulture));
            Operand value = Evaluate(frame, node.Children[0]);
            StageBPrefixOperandMode mode = frame.Function.CaptureModes.Single(capture => capture.Capture == node.Binding.Capture).Mode;
            frame.Captures[node.Binding.Capture] = mode == StageBPrefixOperandMode.Value ? Operand.Value(value.Read()) : value;
            return value;
        }

        private StageBValue Initialize(Operand[] operands, StageBPrefixCall call)
        {
            StageBValue[] arguments = Arguments(call, operands);
            Nethermind.Evm.GasPolicy.TransactionGasInitializationResult result = Nethermind.Evm.GasPolicy.TransactionGasInitializationKernel.TryCreate(
                Unsigned(arguments[0]), Unsigned(arguments[1]), Signed(arguments[2]), Boolean(arguments[3]), Unsigned(arguments[4]));
            return StageBValue.Struct("global::Nethermind.Evm.GasPolicy.TransactionGasInitializationResult",
                ("Outcome", StageBValue.Enum("global::Nethermind.Evm.GasPolicy.TransactionGasInitializationOutcome", (long)result.Outcome)),
                ("Value", StageBValue.UInt64(result.Value)), ("StateReservoir", StageBValue.Int64(result.StateReservoir)),
                ("StateGasUsed", StageBValue.Int64(result.StateGasUsed)), ("StateGasSpill", StageBValue.Int64(result.StateGasSpill)),
                ("StateGasSpillRefunded", StageBValue.Int64(result.StateGasSpillRefunded)));
        }

        private static StageBValue Charge(Operand[] operands, StageBPrefixCall call)
        {
            StageBValue[] arguments = Arguments(call, operands);
            Nethermind.Evm.GasPolicy.StateGasChargeResult result = Nethermind.Evm.GasPolicy.StateGasChargeKernel.TryCharge(
                Unsigned(arguments[0]), Signed(arguments[1]), Signed(arguments[2]), Signed(arguments[3]), Signed(arguments[4]), Signed(arguments[5]));
            return StageBValue.Struct("global::Nethermind.Evm.GasPolicy.StateGasChargeResult",
                ("Outcome", StageBValue.Enum("global::Nethermind.Evm.GasPolicy.StateGasChargeOutcome", (long)result.Outcome)),
                ("Value", StageBValue.UInt64(result.Value)), ("StateReservoir", StageBValue.Int64(result.StateReservoir)),
                ("StateGasUsed", StageBValue.Int64(result.StateGasUsed)), ("StateGasSpill", StageBValue.Int64(result.StateGasSpill)),
                ("StateGasSpillRefunded", StageBValue.Int64(result.StateGasSpillRefunded)));
        }

        private T Request<T>(StageBRequest request) where T : StageBExchange
        {
            T reply = _responses.Take<T>(request);
            _requests.Add(request);
            return reply;
        }

        private static StageBValue[] Arguments(StageBPrefixCall call, Operand[] operands)
        {
            if (call.Arguments.Length == 0 && call.Target.Member.Parameters.Length == operands.Length)
                return operands.Select(static operand => operand.Read()).ToArray();
            StageBValue[] values = new StageBValue[call.Arguments.Length];
            foreach (StageBPrefixArgument argument in call.Arguments) values[argument.Ordinal] = operands[argument.Child].Read();
            return values;
        }

        private static void WriteArgument(StageBPrefixCall call, Operand[] operands, int ordinal, StageBValue value)
        {
            StageBPrefixArgument argument = call.Arguments.Single(argument => argument.Ordinal == ordinal);
            operands[argument.Child].Write(value);
        }

        private void Tick()
        {
            StageBTickResult result = StageBControlKernel.Tick(_fuel);
            if (result.Exhausted) throw new FuelSignal();
            _fuel = result.RemainingFuel;
        }

        private static StageBGasValue Gas(StageBValue value) => new(Unsigned(value.Field("Value")), Signed(value.Field("StateReservoir")),
            Signed(value.Field("StateGasUsed")), Signed(value.Field("StateGasSpill")), Signed(value.Field("StateGasSpillRefunded")));
        private static bool Boolean(StageBValue value) => value.Kind == StageBValueKind.Bool ? value.Boolean : throw new StageBExecutionException("bool", value.Type);
        private static ulong Unsigned(StageBValue value) => value.Kind == StageBValueKind.UInt64 ? value.Unsigned : throw new StageBExecutionException("ulong", value.Type);
        private static long Signed(StageBValue value) => value.Kind is StageBValueKind.Int64 or StageBValueKind.Enum ? value.Signed : throw new StageBExecutionException("long", value.Type);
        private static BigInteger UInt256(StageBValue value) => value.Kind == StageBValueKind.UInt256 ? value.Integer : throw new StageBExecutionException("uint256", value.Type);
        private static string Address(StageBValue value) => value.Kind == StageBValueKind.Address ? value.Text! : throw new StageBExecutionException("address", value.Type);
        private static bool ValueEquals(StageBValue left, StageBValue right) =>
            left.Kind == StageBValueKind.Null && right.Kind == StageBValueKind.Null || left.StructurallyEquals(right);
        private static StageBValue ConvertValue(StageBValue value, string type) => type switch
        {
            "int" when value is { Kind: StageBValueKind.Enum, Type: "byte" } => StageBValue.Enum("int", value.Signed),
            "ulong" when value.Kind is StageBValueKind.Enum or StageBValueKind.Int64 => StageBValue.UInt64(unchecked((ulong)value.Signed)),
            "long" when value.Kind == StageBValueKind.Enum => StageBValue.Int64(value.Signed),
            "global::Nethermind.Evm.EvmExceptionType" when value.Kind == StageBValueKind.Enum => StageBValue.Enum(type, value.Signed),
            _ => value,
        };

        private static StageBValue Constant(string type, string constant)
        {
            if (constant.Length == 0) return Default(type);
            if (constant == "null") return StageBValue.Null(type);
            if (constant == "bool:true") return StageBValue.Bool(true);
            if (constant == "bool:false") return StageBValue.Bool(false);
            if (type == "string" && constant.StartsWith("string:", StringComparison.Ordinal))
                return StageBValue.Reference(type, constant["string:".Length..]);
            int separator = constant.LastIndexOf(':');
            if (separator < 0) throw new StageBExecutionException("constant", constant);
            string value = constant[(separator + 1)..];
            if (constant.StartsWith("System.UInt64:", StringComparison.Ordinal)) return StageBValue.UInt64(ulong.Parse(value, CultureInfo.InvariantCulture));
            if (constant.StartsWith("System.Int64:", StringComparison.Ordinal)) return StageBValue.Int64(long.Parse(value, CultureInfo.InvariantCulture));
            if (constant.StartsWith("System.Int32:", StringComparison.Ordinal) || constant.StartsWith("System.Byte:", StringComparison.Ordinal))
                return StageBValue.Enum(type, long.Parse(value, CultureInfo.InvariantCulture));
            throw new StageBExecutionException("constant", constant);
        }

        private static StageBValue Default(string type)
        {
            if (type == "bool") return StageBValue.Bool(false);
            if (type == "int") return StageBValue.Enum("int", 0);
            if (type == "ulong") return StageBValue.UInt64(0);
            if (type == "long") return StageBValue.Int64(0);
            if (type.Contains("UInt256", StringComparison.Ordinal)) return StageBValue.UInt256(BigInteger.Zero);
            if (type.Contains("EthereumGasPolicy", StringComparison.Ordinal)) return new StageBGasValue(0, 0, 0, 0, 0).ToValue();
            if (type.Contains("ReadOnlyMemory<byte>", StringComparison.Ordinal)) return StageBValue.BytesValue([]);
            if (type.Contains("EvmExceptionType", StringComparison.Ordinal) || type.Contains("ErrorType", StringComparison.Ordinal)) return StageBValue.Enum(type, 0);
            if (type is "" or "void") return StageBValue.Unit;
            if (type.Contains("TransactionResult", StringComparison.Ordinal))
                return StageBValue.Struct(type, ("Error", StageBValue.Enum(type + ".ErrorType", 0)),
                    ("EvmExceptionType", StageBValue.Enum("global::Nethermind.Evm.EvmExceptionType", 0)),
                    ("ErrorDescription", StageBValue.Null("string")));
            if (type.Contains("TransactionSubstate", StringComparison.Ordinal))
                return StageBValue.Struct(type,
                    ("_logs", StageBValue.Null("global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>")),
                    ("_destroyList", StageBValue.Null("global::Nethermind.Core.Collections.JournalSet<global::Nethermind.Core.Address>")),
                    ("Output", StageBValue.BytesValue([])), ("Refund", StageBValue.Int64(0)),
                    ("ShouldRevert", StageBValue.Bool(false)),
                    ("EvmExceptionType", StageBValue.Enum("global::Nethermind.Evm.EvmExceptionType", 0)));
            return StageBValue.Null(type);
        }

        private static string LocalName(string symbol)
        {
            int start = symbol.IndexOf("::local:", StringComparison.Ordinal);
            if (start < 0) return "";
            start += "::local:".Length;
            int end = symbol.IndexOf('@', start);
            return end < 0 ? "" : symbol[start..end];
        }
    }

    private sealed class Frame(StageBPrefixFunction function, Operand @this)
    {
        internal StageBPrefixFunction Function { get; } = function;
        internal Operand This { get; } = @this;
        internal Dictionary<string, Cell> Cells { get; } = new(StringComparer.Ordinal);
        internal Dictionary<int, Operand> Captures { get; } = [];
    }

    private sealed class Cell(string identity, StageBValue value)
    {
        internal string Identity { get; } = identity;
        internal StageBValue Value { get; set; } = value;
        internal Location? Alias { get; set; }
    }

    private readonly record struct Operand(StageBValue? Immediate, Location? Target)
    {
        internal static Operand Value(StageBValue value) => new(value, null);
        internal static Operand Location(Location target) => new(null, target);
        internal StageBValue Read() => Target?.Read() ?? Immediate ?? throw new StageBExecutionException("operand", "empty");
        internal void Write(StageBValue value)
        {
            if (Target is null) throw new StageBExecutionException("location", "value operand");
            Target.Write(value);
        }
    }

    private abstract class Location(bool readOnly)
    {
        protected bool ReadOnly { get; } = readOnly;
        internal abstract string Identity { get; }
        internal abstract StageBValue Read();
        internal abstract void Write(StageBValue value);
        internal abstract StageBResolvedLocation Resolve();
    }

    private sealed class CellLocation(Cell cell, bool readOnly) : Location(readOnly)
    {
        internal Cell Cell { get; } = cell;
        internal override string Identity => Cell.Alias?.Identity ?? Cell.Identity;
        internal override StageBValue Read() => Cell.Alias?.Read() ?? Cell.Value;
        internal override StageBResolvedLocation Resolve()
        {
            StageBResolvedLocation resolved = Cell.Alias?.Resolve() ?? new(Cell, [], ReadOnly, Cell.Identity);
            return resolved with { ReadOnly = ReadOnly || resolved.ReadOnly };
        }
        internal override void Write(StageBValue value)
        {
            if (ReadOnly) throw new StageBExecutionException("readonly", value.Type);
            if (Cell.Alias is { } alias) alias.Write(value);
            else Cell.Value = value;
        }
    }

    private sealed class FieldLocation(Location parent, string fieldName, bool readOnly) : Location(readOnly)
    {
        internal override string Identity => parent.Identity + "." + fieldName;
        internal override StageBValue Read() => parent.Read().Field(fieldName);
        internal override StageBResolvedLocation Resolve()
        {
            StageBResolvedLocation resolved = parent.Resolve();
            return resolved with
            {
                Fields = [.. resolved.Fields, fieldName],
                ReadOnly = ReadOnly || resolved.ReadOnly,
                Provenance = resolved.Provenance + "." + fieldName,
            };
        }
        internal override void Write(StageBValue value)
        {
            if (ReadOnly) throw new StageBExecutionException("readonly", fieldName);
            parent.Write(parent.Read().WithField(fieldName, value));
        }
    }

    private sealed class DiscardLocation(string type) : Location(readOnly: false)
    {
        internal override string Identity => "discard:" + type;
        internal override StageBValue Read() => StageBValue.Null(type);
        internal override StageBResolvedLocation Resolve() => new(this, [], ReadOnly, Identity);
        internal override void Write(StageBValue value) { }
    }

    private sealed class ValueLocation : Location
    {
        private readonly StageBValue _value;
        internal ValueLocation(string identity, StageBValue value) : base(readOnly: true)
        {
            Identity = identity;
            _value = value;
        }
        internal override string Identity { get; }
        internal override StageBValue Read() => _value;
        internal override StageBResolvedLocation Resolve() => new(this, [], ReadOnly, Identity);
        internal override void Write(StageBValue replacement) => throw new StageBExecutionException("readonly", Identity);
    }

    private sealed class SuspendSignal(StageBRefundOperand[] operands) : Exception { internal StageBRefundOperand[] Operands { get; } = operands; }
    private sealed class OutsideSignal : Exception;
    private sealed class FuelSignal : Exception;
}
