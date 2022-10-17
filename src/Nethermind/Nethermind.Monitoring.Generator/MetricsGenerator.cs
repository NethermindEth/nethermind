//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Nethermind.Monitoring.Generator.Attributes;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Nethermind.Monitoring.Generator;

[Generator]
public class MetricsGenerator : ISourceGenerator
{
    private struct MetricData
    {
        public MetricData(params string[] args) : this(args[0], args[1], args[2]) { }
        public MetricData(string typename, string propertyName, string description)
        {
            Typename = typename;
            Description = description;
            PropertyName = propertyName;
        }
        public string Typename { get; }
        public string Description { get; }
        public string PropertyName { get; }
    }
    private string EmitProps(IEnumerable<MetricData> metrics)
    => metrics
    .Select(metric => @$"[Description(""{metric.Description}"")] public static {metric.Typename} {metric.PropertyName} {{ get; set; }}")
    .Aggregate((a, b) => $"{a}\n\t{b}");
    private string PartialMetrics(IEnumerable<MetricData> metrics) => @$"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugin;
public static partial class Metrics
{{
{EmitProps(metrics)}
}}
";
    public void Execute(GeneratorExecutionContext context)
    {
        var attributeToLookFor = typeof(MetricsAttribute);
        var allFunctionsInCompilationUnit = GetMarkedFunctionBy(attributeToLookFor, context.Compilation);
        var allAttributesToBeEmited = GetTargetAttributesFrom(allFunctionsInCompilationUnit, attributeToLookFor, context.Compilation);

        var classToEmitSourceCode = PartialMetrics(allAttributesToBeEmited);

        context.AddSource("Metrics.g.cs", SourceText.From(classToEmitSourceCode, Encoding.UTF8));
    }

    public void Initialize(GeneratorInitializationContext context)
    {

    }

    private bool IsAttribute(Type flag) => flag.Name.EndsWith("Attribute") && flag.BaseType == typeof(System.Attribute);

    private MethodDeclarationSyntax[] GetMarkedFunctionBy(Type flagType /* must be an attribute type*/, Compilation context)
    {
        if (!IsAttribute(flagType))
        {
            return Array.Empty<MethodDeclarationSyntax>();
        }

        IEnumerable<SyntaxNode> allNodes = context.SyntaxTrees.SelectMany(s => s.GetRoot().DescendantNodes());
        return allNodes
            .Where(d => d.IsKind(SyntaxKind.MethodDeclaration))
            .OfType<MethodDeclarationSyntax>()
            .ToArray();
    }

    private MetricData[] GetTargetAttributesFrom(IEnumerable<MethodDeclarationSyntax> allFunctions, Type flagType, Compilation context)
    { 
        return allFunctions.SelectMany(funcDef =>
        {
            var semanticModel = context.GetSemanticModel(funcDef.SyntaxTree);
            return funcDef.AttributeLists
                .SelectMany(x => x.Attributes)
                .Where(attr => flagType.Name.StartsWith(attr.Name.ToString()))
                .Select(attr => attr.ArgumentList.Arguments
                                    .Select(arg => semanticModel
                                                    .GetConstantValue(arg.Expression)
                                                    .ToString())
                                    .ToArray())
                .ToArray();
        })
        .Select(args => new MetricData(args))
        .ToArray();
    }
}
