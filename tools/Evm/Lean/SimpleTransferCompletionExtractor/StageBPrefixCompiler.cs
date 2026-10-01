// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal static class StageBPrefixCompiler
{
    internal static StageBPrefixProgram Compile(StageBPlan plan) => new Compiler(plan).Compile();

    private sealed class Compiler(StageBPlan plan)
    {
        private readonly Dictionary<string, StageBMethod> _methods = plan.Methods.ToDictionary(static method => method.Symbol, StringComparer.Ordinal);
        private readonly Dictionary<string, StageBMember> _members = plan.Members.ToDictionary(static member => member.Symbol, StringComparer.Ordinal);
        private readonly Dictionary<string, StageBCallee> _callees = plan.Callees.ToDictionary(static callee => callee.Symbol, StringComparer.Ordinal);
        private readonly Dictionary<string, StageBPrefixFunction> _functions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly HashSet<string> _initializers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedMembers = new(StringComparer.Ordinal);
        private readonly List<string> _topological = [];
        private readonly List<StageBPrefixSuspension> _suspensions = [];
        private readonly StageBPrefixAnchors _anchors = plan.PrefixAnchors ?? throw Error("anchors", "missing");

        internal StageBPrefixProgram Compile()
        {
            if (!_methods.TryGetValue(_anchors.Entry, out StageBMethod? entry) || entry.SelectedEntry is null)
                throw Error("entry", "successful nonce continuation");
            if (!_methods.TryGetValue(_anchors.SimpleTransfer, out StageBMethod? simpleSource) ||
                Terms(simpleSource.ControlFlowEvidence.SelectMany(static block => block.BranchValue is null
                    ? block.Operations : [.. block.Operations, block.BranchValue]))
                    .Count(term => term.Kind == StageBOperationKind.Invocation && term.Symbol == _anchors.Refund) != 1)
                throw Error("refund-anchor", "expected one invocation");
            StageBPrefixFunction root = Function(_anchors.Entry);
            if (_suspensions is not [StageBPrefixSuspension suspension]) throw Error("refund-count", _suspensions.Count.ToString());
            if (!_functions.TryGetValue(_anchors.SimpleTransfer, out StageBPrefixFunction? simple) || simple.MayReturn)
                throw Error("simple-transfer-exit", "refund suspension required");
            StageBMethod feeHelper = plan.Methods.Single(static method => method.Signature?.Name == "UpdateHeaderGasUsedAndPayFees");
            StageBInertFunction inertFeeHelper = new(feeHelper.Signature!,
                feeHelper.SelectedEntry ?? new(feeHelper.ControlFlowEvidence[0].Ordinal, 0), feeHelper.ControlFlowEvidence);
            StageBPrefixProgram program = new(_anchors.Entry, _anchors.PolicyType, _topological.Select(symbol => _functions[symbol]).ToArray(),
                _initializers.Order(StringComparer.Ordinal).ToArray(), _usedMembers.Order(StringComparer.Ordinal).Select(symbol => _members[symbol]).ToArray(),
                plan.Types, suspension, _topological.ToArray(), root.FuelBound, plan.Scope, plan.CompilerSources, plan.CompilerReferences,
                plan.ExternalPremises, plan.AcceptedStages, inertFeeHelper, "");
            return program with { Integrity = StageBPrefixIntegrity.Compute(program) };
        }

        private StageBPrefixFunction Function(string symbol)
        {
            if (_functions.TryGetValue(symbol, out StageBPrefixFunction? compiled)) return compiled;
            if (_anchors.AfterRefundMembers.Contains(symbol, StringComparer.Ordinal)) throw Error("after-refund-call", _members[symbol].Name);
            if (!_active.Add(symbol)) throw Error("recursion", symbol);
            if (!_methods.TryGetValue(symbol, out StageBMethod? source) || source.Signature is not { } signature || source.ControlFlowEvidence.Length == 0)
                throw Error("body", symbol);
            Dictionary<int, StageBBlock> sourceBlocks = source.ControlFlowEvidence.ToDictionary(static block => block.Ordinal);
            Dictionary<int, StageBPrefixBlock> blocks = [];
            HashSet<int> activeBlocks = [];
            HashSet<string> calls = new(StringComparer.Ordinal);
            StageBCfgPoint entry = source.SelectedEntry ?? new(source.ControlFlowEvidence[0].Ordinal, 0);
            Block(entry.Block, entry.Operation);
            Dictionary<int, long> bounds = [];
            long fuel = Bound(entry.Block);
            bool mayReturn = blocks.Values.Any(static block => block.Exit == StageBPrefixExit.Return);
            Dictionary<int, StageBPrefixOperandMode> captureModes = [];
            bool changed;
            do
            {
                changed = false;
                foreach (StageBPrefixBlock block in blocks.Values)
                {
                    foreach (StageBPrefixNode node in block.Operations) DiscoverCaptureMode(node, node.Mode);
                    if (block.BranchValue is { } branch) DiscoverCaptureMode(branch, branch.Mode);
                }
            } while (changed);
            foreach (int ordinal in blocks.Keys.ToArray())
            {
                StageBPrefixBlock block = blocks[ordinal];
                blocks[ordinal] = block with
                {
                    Operations = block.Operations.Select(node => BindCaptureMode(node, node.Mode)).ToArray(),
                    BranchValue = block.BranchValue is { } branch ? BindCaptureMode(branch, branch.Mode) : null,
                };
            }
            for (int index = 0; index < _suspensions.Count; index++)
                if (_suspensions[index].Continuation.Function == symbol)
                    _suspensions[index] = _suspensions[index] with
                    {
                        OrderedOperands = _suspensions[index].OrderedOperands.Select(node => BindCaptureMode(node, node.Mode)).ToArray(),
                    };
            compiled = new(signature, entry, source.Bindings, source.Captures, source.Regions, source.EntryFacts,
                blocks.Values.OrderBy(static block => block.Ordinal).ToArray(), calls.Order(StringComparer.Ordinal).ToArray(),
                source.Captures.Select(capture => new StageBPrefixCaptureMode(capture.Id, captureModes.GetValueOrDefault(capture.Id))).ToArray(),
                bounds.OrderBy(static pair => pair.Key).Select(static pair => new StageBPrefixBlockBound(pair.Key, pair.Value)).ToArray(), fuel, mayReturn);
            _active.Remove(symbol);
            _functions.Add(symbol, compiled);
            _topological.Add(symbol);
            return compiled;

            void Block(int ordinal, int firstOperation = 0)
            {
                if (activeBlocks.Contains(ordinal)) throw Error("cfg-cycle", symbol);
                if (blocks.ContainsKey(ordinal)) return;
                if (!sourceBlocks.TryGetValue(ordinal, out StageBBlock? block) || !block.Reachable) throw Error("cfg-edge", symbol);
                activeBlocks.Add(ordinal);
                List<StageBPrefixNode> operations = [];
                StageBPrefixNode? branch = null;
                StageBPrefixExit exit = StageBPrefixExit.Branch;
                StageBTerm[] terms = block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue];
                if (firstOperation < 0 || firstOperation > terms.Length) throw Error("entry-operation", symbol);
                for (int index = firstOperation; index < terms.Length; index++)
                {
                    StageBTerm term = terms[index];
                    if (term.Kind == StageBOperationKind.OutsideSelectedDomain)
                    {
                        exit = StageBPrefixExit.OutsideSelectedDomain;
                        break;
                    }
                    StageBPrefixNode node = Node(term, StageBPrefixOperandMode.Value, []);
                    if (index == block.Operations.Length) branch = node;
                    else operations.Add(node);
                    if (!node.Completes)
                    {
                        exit = StageBPrefixExit.Suspend;
                        break;
                    }

                    StageBPrefixNode Node(StageBTerm term, StageBPrefixOperandMode mode, int[] path)
                    {
                        RequireOperation(term);
                        StageBTermBinding binding = term.Binding ?? throw Error("binding", term.Kind.ToString());
                        StageBPrefixTarget? target = binding.Call is { } site ? Target(site.Target) : null;
                        List<StageBPrefixNode> children = [];
                        bool completes = true;
                        for (int childIndex = 0; childIndex < term.Children.Length; childIndex++)
                        {
                            StageBTerm child = term.Children[childIndex];
                            StageBPrefixOperandMode childMode = OperandMode(term, childIndex, mode, target);
                            StageBPrefixNode compiledChild = Node(child, childMode, [.. path, childIndex]);
                            children.Add(compiledChild);
                            if (!compiledChild.Completes) { completes = false; break; }
                        }
                        StageBPrefixCall? call = target is null ? null : Call(term, target);
                        StageBPrefixTarget[] additional = binding.AdditionalMembers.Select(Target).ToArray();
                        if (completes && target is { Kind: StageBPrefixTargetKind.RefundSuspension })
                        {
                            RequireRefund(term, call!);
                            if (symbol != _anchors.SimpleTransfer) throw Error("refund-owner", symbol);
                            _suspensions.Add(new(call!, children.ToArray(), new(symbol, ordinal, index, path, terms[index], source.ControlFlowEvidence)));
                            completes = false;
                        }
                        else if (completes && target is { Kind: StageBPrefixTargetKind.Local } && !Function(target.Body).MayReturn)
                            completes = false;
                        return new(term.Kind, term.Type, term.Symbol, term.Operator, term.Constant, term.Conversion, term.Implicit, mode,
                            binding, call, additional, children.ToArray(), completes);
                    }
                }
                if (exit == StageBPrefixExit.Branch)
                {
                    if (block.Kind == "Exit" || block.FallThrough?.Semantics == "Return" && block.FallThrough.Destination < 0)
                        exit = StageBPrefixExit.Return;
                    else
                    {
                        Edge(block.FallThrough);
                        Edge(block.Conditional);
                    }
                }
                blocks.Add(ordinal, new(ordinal, operations.ToArray(), branch, block.ConditionKind, exit,
                    exit == StageBPrefixExit.Branch ? block.FallThrough : null, exit == StageBPrefixExit.Branch ? block.Conditional : null));
                activeBlocks.Remove(ordinal);
            }

            void Edge(StageBEdge? edge)
            {
                if (edge is null) return;
                if (edge.FinallyRegions.Length != 0 || edge.Semantics is not ("Regular" or "Return"))
                    throw Error("cfg-semantics", edge.Semantics);
                if (edge.Destination < 0) throw Error("cfg-destination", symbol);
                Block(edge.Destination);
            }

            StageBPrefixTarget Target(string targetSymbol)
            {
                if (!_members.TryGetValue(targetSymbol, out StageBMember? member) || !_callees.TryGetValue(targetSymbol, out StageBCallee? callee))
                    throw Error("ownership", targetSymbol);
                _usedMembers.Add(targetSymbol);
                StageBPrefixTargetKind kind;
                string body = "";
                if (callee.Owner == StageBCalleeOwner.Local)
                {
                    if (callee.Contract == "CLR value-type default; zero fields and null references") kind = StageBPrefixTargetKind.DefaultValue;
                    else
                    {
                        kind = StageBPrefixTargetKind.Local;
                        body = callee.Contract;
                        Function(body);
                        if (member.Kind == StageBMemberKind.Field)
                        {
                            kind = StageBPrefixTargetKind.StaticField;
                            _initializers.Add(body);
                        }
                        else calls.Add(body);
                    }
                }
                else if (callee.Owner == StageBCalleeOwner.ExternalRequest)
                {
                    if (member.Name is "ReportAccess" or "ReportFees" || callee.RequestKind == StageBRequestKind.Pool)
                        throw Error("after-refund-call", member.Name);
                    kind = StageBPrefixTargetKind.ExternalRequest;
                }
                else
                {
                    StageBStage[] stages = plan.AcceptedStages.Where(stage => stage.EntryPoints.Contains(member.Definition, StringComparer.Ordinal)).ToArray();
                    if (stages is not [StageBStage stage] || stage.Name != callee.Contract) throw Error("stage-entry", targetSymbol);
                    kind = stage.Name switch
                    {
                        "transaction-initialization" => StageBPrefixTargetKind.Initialization,
                        "state-charge" => StageBPrefixTargetKind.StateCharge,
                        "ordinary-refund" when member.Definition == _anchors.Refund => StageBPrefixTargetKind.RefundSuspension,
                        _ => throw Error("after-refund-stage", stage.Name),
                    };
                }
                return new(kind, member, body, callee.RequestKind);
            }

            long Bound(int ordinal)
            {
                if (bounds.TryGetValue(ordinal, out long bound)) return bound;
                StageBPrefixBlock block = blocks[ordinal];
                bound = 1;
                foreach (StageBPrefixNode node in block.Operations) bound = checked(bound + NodeBound(node));
                if (block.BranchValue is { } branch) bound = checked(bound + NodeBound(branch));
                long fallThrough = block.FallThrough is { Destination: >= 0 } fall ? Bound(fall.Destination) : 0;
                long conditional = block.Conditional is { Destination: >= 0 } conditionalEdge ? Bound(conditionalEdge.Destination) : 0;
                bound = checked(bound + Math.Max(fallThrough, conditional));
                bounds.Add(ordinal, bound);
                return bound;
            }

            void DiscoverCaptureMode(StageBPrefixNode node, StageBPrefixOperandMode mode)
            {
                if (node.Kind == StageBOperationKind.FlowCaptureReference && mode != StageBPrefixOperandMode.Value)
                {
                    StageBPrefixOperandMode previous = captureModes.GetValueOrDefault(node.Binding.Capture);
                    if (previous == StageBPrefixOperandMode.Value || previous == StageBPrefixOperandMode.ReadOnlyLocation && mode == StageBPrefixOperandMode.Location)
                    {
                        captureModes[node.Binding.Capture] = mode;
                        changed = true;
                    }
                }
                foreach (StageBPrefixNode child in node.Children)
                    DiscoverCaptureMode(child, CapturedOperandMode(node, child, mode));
            }

            StageBPrefixOperandMode CapturedOperandMode(StageBPrefixNode node, StageBPrefixNode child, StageBPrefixOperandMode mode) => node.Kind switch
            {
                StageBOperationKind.FlowCapture => captureModes.GetValueOrDefault(node.Binding.Capture),
                StageBOperationKind.DeclarationExpression => mode,
                StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference
                    when node.Call?.Target.Member.Receiver == StageBReceiverKind.Value => mode,
                _ => child.Mode,
            };

            StageBPrefixNode BindCaptureMode(StageBPrefixNode node, StageBPrefixOperandMode mode) => node with
            {
                Mode = mode,
                Children = node.Children.Select(child => BindCaptureMode(child, CapturedOperandMode(node, child, mode))).ToArray(),
            };
        }

        private long NodeBound(StageBPrefixNode node)
        {
            long bound = 1;
            foreach (StageBPrefixNode child in node.Children) bound = checked(bound + NodeBound(child));
            if (node.Call?.Target is { Kind: StageBPrefixTargetKind.Local or StageBPrefixTargetKind.StaticField } target)
                bound = checked(bound + _functions[target.Body].FuelBound);
            foreach (StageBPrefixTarget additional in node.AdditionalTargets)
                if (additional.Kind == StageBPrefixTargetKind.Local) bound = checked(bound + _functions[additional.Body].FuelBound);
            return bound;
        }

        private static StageBPrefixCall Call(StageBTerm term, StageBPrefixTarget target)
        {
            StageBCallSite site = term.Binding!.Call!;
            List<StageBPrefixArgument> arguments = [];
            foreach (int index in site.ArgumentChildren)
            {
                if (index < 0 || index >= term.Children.Length || term.Children[index] is not { Kind: StageBOperationKind.Argument, Binding: { } binding } argument)
                    throw Error("argument", target.Member.Name);
                if (argument.ParameterOrdinal < 0 || argument.ParameterOrdinal >= target.Member.Parameters.Length || argument.Children.Length != 1)
                    throw Error("argument-ordinal", target.Member.Name);
                arguments.Add(new(index, argument.ParameterOrdinal, binding.ArgumentMode, argument.Implicit, argument.ArgumentKind));
            }
            if (term.Kind is StageBOperationKind.Invocation or StageBOperationKind.ObjectCreation &&
                (arguments.Count != target.Member.Parameters.Length || arguments.Select(static argument => argument.Ordinal).Distinct().Count() != arguments.Count))
                throw Error("argument-count", target.Member.Name);
            return new(target, site.ReceiverChild, arguments.ToArray());
        }

        private static void RequireRefund(StageBTerm term, StageBPrefixCall call)
        {
            if (term.Kind != StageBOperationKind.Invocation || call.Target.Member.Parameters.Length != 12 || call.Arguments.Length != 12)
                throw Error("refund-arguments", "expected twelve");
            if (call.ReceiverChild != 0 || term.Children[0] is not { Kind: StageBOperationKind.InstanceReference, Operator: "ContainingTypeInstance" })
                throw Error("refund-receiver", "this required");
            StageBPrefixArgument final = call.Arguments.Single(static argument => argument.Ordinal == 11);
            if (term.Children[final.Child].Children[0].Constant != "bool:false")
                throw Error("refund-default", "false required");
            foreach (StageBPrefixArgument argument in call.Arguments)
            {
                StageBRefKind expected = call.Target.Member.Parameters[argument.Ordinal].RefKind;
                if (expected == StageBRefKind.In && argument.Mode != StageBArgumentMode.ReadOnlyLocation ||
                    expected == StageBRefKind.None && argument.Mode != StageBArgumentMode.Value)
                    throw Error("refund-passing", argument.Ordinal.ToString());
            }
        }

        private static StageBPrefixOperandMode OperandMode(StageBTerm parent, int child, StageBPrefixOperandMode mode, StageBPrefixTarget? target) => parent.Kind switch
        {
            StageBOperationKind.Argument => parent.Binding!.ArgumentMode switch
            {
                StageBArgumentMode.ReadOnlyLocation => StageBPrefixOperandMode.ReadOnlyLocation,
                StageBArgumentMode.WritableLocation or StageBArgumentMode.OutLocation => StageBPrefixOperandMode.Location,
                _ => StageBPrefixOperandMode.Value,
            },
            StageBOperationKind.SimpleAssignment when child == 0 => StageBPrefixOperandMode.Location,
            StageBOperationKind.SimpleAssignment when parent.Binding!.IsRef => StageBPrefixOperandMode.ReadOnlyLocation,
            StageBOperationKind.DeclarationExpression => mode,
            StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference
                when target?.Member.Receiver == StageBReceiverKind.Value => mode,
            _ => StageBPrefixOperandMode.Value,
        };

        private static void RequireOperation(StageBTerm term)
        {
            if (term.Kind is not (StageBOperationKind.ExpressionStatement or StageBOperationKind.SimpleAssignment or
                StageBOperationKind.Invocation or StageBOperationKind.ObjectCreation or StageBOperationKind.CollectionExpression or
                StageBOperationKind.ArrayCreation or StageBOperationKind.ArrayInitializer or StageBOperationKind.FieldReference or
                StageBOperationKind.PropertyReference or StageBOperationKind.LocalReference or StageBOperationKind.ParameterReference or
                StageBOperationKind.InstanceReference or StageBOperationKind.Literal or StageBOperationKind.DefaultValue or
                StageBOperationKind.Binary or StageBOperationKind.Unary or StageBOperationKind.Conversion or StageBOperationKind.Argument or
                StageBOperationKind.IsPattern or StageBOperationKind.ConstantPattern or StageBOperationKind.NegatedPattern or
                StageBOperationKind.IsNull or StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or
                StageBOperationKind.DeclarationExpression or StageBOperationKind.Discard)) throw Error("operation", term.Kind.ToString());
            if (term.Binding?.AdditionalMembers.Length > 0 && term.Kind is not (StageBOperationKind.CollectionExpression or
                StageBOperationKind.Conversion or StageBOperationKind.Argument)) throw Error("additional-member", term.Kind.ToString());
        }

        private static IEnumerable<StageBTerm> Terms(IEnumerable<StageBTerm> terms)
        {
            foreach (StageBTerm term in terms)
            {
                yield return term;
                foreach (StageBTerm child in Terms(term.Children)) yield return child;
            }
        }
    }

    private static ExtractionException Error(string code, string detail) => new($"Stage-B prefix {code}: {detail}.");
}
