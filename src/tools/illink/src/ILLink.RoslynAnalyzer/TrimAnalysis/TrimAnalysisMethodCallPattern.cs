// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using ILLink.RoslynAnalyzer.DataFlow;
using ILLink.Shared.DataFlow;
using ILLink.Shared.TrimAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiValue = ILLink.Shared.DataFlow.ValueSet<ILLink.Shared.DataFlow.SingleValue>;

namespace ILLink.RoslynAnalyzer.TrimAnalysis
{
    internal readonly record struct TrimAnalysisMethodCallPattern
    {
        public IMethodSymbol CalledMethod { get; init; }
        public MultiValue Instance { get; init; }
        public ImmutableArray<MultiValue> Arguments { get; init; }
        public IOperation Operation { get; init; }
        public ISymbol OwningSymbol { get; init; }
        public FeatureContext FeatureContext { get; init; }

        public TrimAnalysisMethodCallPattern(
            IMethodSymbol calledMethod,
            MultiValue instance,
            ImmutableArray<MultiValue> arguments,
            IOperation operation,
            ISymbol owningSymbol,
            FeatureContext featureContext)
        {
            CalledMethod = calledMethod;
            Instance = instance.DeepCopy();
            if (arguments.IsEmpty)
            {
                Arguments = ImmutableArray<MultiValue>.Empty;
            }
            else
            {
                var builder = ImmutableArray.CreateBuilder<MultiValue>();
                foreach (var argument in arguments)
                {
                    builder.Add(argument.DeepCopy());
                }
                Arguments = builder.ToImmutableArray();
            }
            Operation = operation;
            OwningSymbol = owningSymbol;
            FeatureContext = featureContext.DeepCopy();
        }

        public TrimAnalysisMethodCallPattern Merge(
            ValueSetLattice<SingleValue> lattice,
            FeatureContextLattice featureContextLattice,
            TrimAnalysisMethodCallPattern other)
        {
            Debug.Assert(Operation == other.Operation);
            Debug.Assert(SymbolEqualityComparer.Default.Equals(CalledMethod, other.CalledMethod));
            Debug.Assert(SymbolEqualityComparer.Default.Equals(OwningSymbol, other.OwningSymbol));
            Debug.Assert(Arguments.Length == other.Arguments.Length);

            var argumentsBuilder = ImmutableArray.CreateBuilder<MultiValue>();
            for (int i = 0; i < Arguments.Length; i++)
            {
                argumentsBuilder.Add(lattice.Meet(Arguments[i], other.Arguments[i]));
            }

            return new TrimAnalysisMethodCallPattern(
                CalledMethod,
                lattice.Meet(Instance, other.Instance),
                argumentsBuilder.ToImmutable(),
                Operation,
                OwningSymbol,
                featureContextLattice.Meet(FeatureContext, other.FeatureContext));
        }

        public void ReportDiagnostics(DataFlowAnalyzerContext context, Action<Diagnostic> reportDiagnostic)
        {
            Location location = Operation.Syntax.GetLocation();
            if (context.EnableTrimAnalyzer &&
                !OwningSymbol.IsInRequiresUnreferencedCodeAttributeScope(out _) &&
                !FeatureContext.IsEnabled(RequiresUnreferencedCodeAnalyzer.FullyQualifiedRequiresUnreferencedCodeAttribute))
            {
                TrimAnalysisVisitor.HandleCall(Operation, OwningSymbol, CalledMethod, Instance, Arguments, location, reportDiagnostic, default, out var _);
            }
            // For Requires, make the location the reference to the method, not the entire invocation.
            // The parameters are not part of the issue, and including them in the location can be misleading.
            location = Operation.Syntax switch
            {
                InvocationExpressionSyntax invocationSyntax => invocationSyntax.Expression.GetLocation(),
                _ => location
            };
            var diagnosticContext = new DiagnosticContext(location, reportDiagnostic);
            foreach (var requiresAnalyzer in context.EnabledRequiresAnalyzers)
            {
                if (!requiresAnalyzer.IsIntrinsicallyHandled(CalledMethod, Instance, Arguments))
                    requiresAnalyzer.CheckAndCreateRequiresDiagnostic(Operation, CalledMethod, OwningSymbol, context, FeatureContext, in diagnosticContext);
            }
        }
    }
}
