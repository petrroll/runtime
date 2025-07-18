// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ILLink.RoslynAnalyzer.DataFlow;
using ILLink.RoslynAnalyzer.TrimAnalysis;
using ILLink.Shared.DataFlow;
using ILLink.Shared.TrimAnalysis;
using ILLink.Shared.TypeSystemProxy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

using MultiValue = ILLink.Shared.DataFlow.ValueSet<ILLink.Shared.DataFlow.SingleValue>;
using StateValue = ILLink.RoslynAnalyzer.DataFlow.LocalDataFlowState<
    ILLink.Shared.DataFlow.ValueSet<ILLink.Shared.DataFlow.SingleValue>,
    ILLink.RoslynAnalyzer.DataFlow.FeatureContext,
    ILLink.Shared.DataFlow.ValueSetLattice<ILLink.Shared.DataFlow.SingleValue>,
    ILLink.RoslynAnalyzer.DataFlow.FeatureContextLattice
    >;

namespace ILLink.RoslynAnalyzer.TrimAnalysis
{
    internal sealed class TrimAnalysisVisitor : LocalDataFlowVisitor<
        MultiValue,
        FeatureContext,
        ValueSetLattice<SingleValue>,
        FeatureContextLattice,
        FeatureChecksValue>
    {
        public readonly TrimAnalysisPatternStore TrimAnalysisPatterns;

        private readonly ValueSetLattice<SingleValue> _multiValueLattice;

        // Limit tracking array values to 32 values for performance reasons.
        // There are many arrays much longer than 32 elements in .NET,
        // but the interesting ones for the ILLink are nearly always less than 32 elements.
        private const int MaxTrackedArrayValues = 32;

        private FeatureChecksVisitor _featureChecksVisitor;

        public TrimAnalysisVisitor(
            Compilation compilation,
            LocalStateAndContextLattice<MultiValue, FeatureContext, ValueSetLattice<SingleValue>, FeatureContextLattice> lattice,
            ISymbol owningSymbol,
            ControlFlowGraph methodCFG,
            ImmutableDictionary<CaptureId, FlowCaptureKind> lValueFlowCaptures,
            TrimAnalysisPatternStore trimAnalysisPatterns,
            InterproceduralState<MultiValue, ValueSetLattice<SingleValue>> interproceduralState,
            DataFlowAnalyzerContext dataFlowAnalyzerContext)
            : base(compilation, lattice, owningSymbol, methodCFG, lValueFlowCaptures, interproceduralState)
        {
            _multiValueLattice = lattice.LocalStateLattice.Lattice.ValueLattice;
            TrimAnalysisPatterns = trimAnalysisPatterns;
            _featureChecksVisitor = new FeatureChecksVisitor(dataFlowAnalyzerContext);
        }

        public override FeatureChecksValue GetConditionValue(IOperation branchValueOperation, StateValue state)
        {
            return _featureChecksVisitor.Visit(branchValueOperation, state);
        }

        public override void ApplyCondition(FeatureChecksValue featureChecksValue, ref LocalStateAndContext<MultiValue, FeatureContext> currentState)
        {
            currentState.Context = currentState.Context.Union(new FeatureContext(featureChecksValue.EnabledFeatures));
        }

        // Override visitor methods to create tracked values when visiting operations
        // which reference possibly annotated source locations:
        // - parameters
        // - 'this' parameter (for annotated methods)
        // - field reference

        public override MultiValue DefaultVisit(IOperation operation, StateValue argument)
        {
            var returnValue = base.DefaultVisit(operation, argument);

            // If the return value is empty (TopValue basically) and the Operation tree
            // reports it as having a constant value, use that as it will automatically cover
            // cases we don't need/want to handle.
            if (!returnValue.IsEmpty())
                return returnValue;

            if (TryGetConstantValue(operation, out var constValue))
                return constValue;

            if (operation.Type is not null)
                return UnknownValue.Instance;

            return returnValue;
        }

        public override MultiValue VisitArrayCreation(IArrayCreationOperation operation, StateValue state)
        {
            var value = base.VisitArrayCreation(operation, state);

            // Don't track multi-dimensional arrays
            if (operation.DimensionSizes.Length != 1)
                return TopValue;

            // Don't track large arrays for performance reasons
            if (operation.Initializer?.ElementValues.Length >= MaxTrackedArrayValues)
                return TopValue;

            var arrayValue = ArrayValue.Create(Visit(operation.DimensionSizes[0], state));
            var elements = operation.Initializer?.ElementValues.Select(val => Visit(val, state)).ToArray() ?? System.Array.Empty<MultiValue>();
            foreach (var array in arrayValue.AsEnumerable().Cast<ArrayValue>())
            {
                for (int i = 0; i < elements.Length; i++)
                {
                    array.IndexValues.Add(i, ArrayValue.SanitizeArrayElementValue(elements[i]));
                }
            }

            return arrayValue;
        }

        public override MultiValue VisitConversion(IConversionOperation operation, StateValue state)
        {
            var value = base.VisitConversion(operation, state);

            if (operation.OperatorMethod is IMethodSymbol method)
                return method.ReturnType.IsTypeInterestingForDataflow(isByRef: method.ReturnsByRef) ? new MethodReturnValue(method, isNewObj: false) : value;

            // TODO - is it possible to have annotation on the operator method parameters?
            // if so, will these be checked here?

            return value;
        }

        public override MultiValue VisitParameterReference(IParameterReferenceOperation paramRef, StateValue state)
        {
            // Reading from a parameter always returns the same annotated value. We don't track modifications.
            return GetParameterTargetValue(paramRef.Parameter);
        }

        public override MultiValue VisitInstanceReference(IInstanceReferenceOperation instanceRef, StateValue state)
        {
            if (instanceRef.ReferenceKind != InstanceReferenceKind.ContainingTypeInstance)
                return TopValue;

            // The instance reference operation represents a 'this' or 'base' reference to the containing type,
            // so we get the annotation from the containing method.
            // 'this' is not allowed in field/property initializers, so the owning symbol should be a method.
            // It can also happen that we see this for a static method - for example a delegate creation
            // over a local function does this, even thought the "this" makes no sense inside a static scope.
            if (OwningSymbol is IMethodSymbol method && !method.IsStatic)
                return new MethodParameterValue(method, (ParameterIndex)0, FlowAnnotations.GetMethodParameterAnnotation(new ParameterProxy(new(method), (ParameterIndex)0)));

            return TopValue;
        }

        public override MultiValue VisitFieldReference(IFieldReferenceOperation fieldRef, StateValue state)
        {
            var field = fieldRef.Field;
            switch (field.Name)
            {
                case "EmptyTypes" when field.ContainingType.IsTypeOf("System", "Type"):
#if DEBUG
                case "ArrayField" when field.ContainingType.IsTypeOf("Mono.Linker.Tests.Cases.DataFlow", "WriteArrayField"):
#endif
                {
                    return ArrayValue.Create(0);
                }
                case "Empty" when field.ContainingType.IsTypeOf("System", "String"):
                {
                    return new KnownStringValue(string.Empty);
                }
            }

            if (TryGetConstantValue(fieldRef, out var constValue))
                return constValue;

            var current = state.Current;
            return GetFieldTargetValue(fieldRef, in current.Context);
        }

        public override MultiValue VisitTypeOf(ITypeOfOperation typeOfOperation, StateValue state)
        {
            return SingleValueExtensions.FromTypeSymbol(typeOfOperation.TypeOperand) ?? TopValue;
        }

        public override MultiValue VisitBinaryOperator(IBinaryOperation operation, StateValue argument)
        {
            if (!operation.ConstantValue.HasValue && // Optimization - if there is already a constant value available, rely on the Visit(IOperation) instead
                operation.OperatorKind == BinaryOperatorKind.Or &&
                operation.OperatorMethod is null &&
                (operation.Type?.TypeKind == TypeKind.Enum || operation.Type?.SpecialType == SpecialType.System_Int32))
            {
                MultiValue leftValue = Visit(operation.LeftOperand, argument);
                MultiValue rightValue = Visit(operation.RightOperand, argument);

                MultiValue result = TopValue;
                foreach (var left in leftValue.AsEnumerable())
                {
                    if (left is UnknownValue)
                        result = _multiValueLattice.Meet(result, left);
                    else if (left is ConstIntValue leftConstInt)
                    {
                        foreach (var right in rightValue.AsEnumerable())
                        {
                            if (right is UnknownValue)
                                result = _multiValueLattice.Meet(result, right);
                            else if (right is ConstIntValue rightConstInt)
                            {
                                result = _multiValueLattice.Meet(result, new ConstIntValue(leftConstInt.Value | rightConstInt.Value));
                            }
                        }
                    }
                }

                return result;
            }

            return base.VisitBinaryOperator(operation, argument);
        }

        // Override handlers for situations where annotated locations may be involved in reflection access:
        // - assignments
        // - method calls
        // - value returned from a method

        public override MultiValue GetFieldTargetValue(IFieldReferenceOperation fieldReference, in FeatureContext featureContext)
        {
            var field = fieldReference.Field;

            TrimAnalysisPatterns.Add(
                new TrimAnalysisFieldAccessPattern(field, fieldReference, OwningSymbol, featureContext)
            );

            ProcessGenericArgumentDataFlow(field, fieldReference, featureContext);

            return new FieldValue(field);
        }

        public override MultiValue GetBackingFieldTargetValue(IPropertyReferenceOperation propertyReference, in FeatureContext featureContext)
        {
            var property = propertyReference.Property;

            TrimAnalysisPatterns.Add(
                new TrimAnalysisBackingFieldAccessPattern(propertyReference.Property, propertyReference, OwningSymbol, featureContext)
            );

            ProcessGenericArgumentDataFlow(property, propertyReference, featureContext);

            return new FieldValue(property);
        }


        public override MultiValue GetParameterTargetValue(IParameterSymbol parameter)
            => new MethodParameterValue(parameter);

        public override void HandleAssignment(MultiValue source, MultiValue target, IOperation operation, in FeatureContext featureContext)
        {
            if (target.Equals(TopValue))
                return;

            // TODO: consider not tracking patterns unless the target is something
            // annotated with DAMT.
            TrimAnalysisPatterns.Add(
                // This will copy the values if necessary
                new TrimAnalysisAssignmentPattern(source, target, operation, OwningSymbol, featureContext));
        }

        public override MultiValue HandleArrayElementRead(MultiValue arrayValue, MultiValue indexValue, IOperation operation)
        {
            if (arrayValue.AsSingleValue() is ArrayOfAnnotatedSystemTypeValue arrayOfAnnotated && !arrayOfAnnotated.IsModified)
            {
                return arrayOfAnnotated.GetAnyElementValue();
            }

            if (indexValue.AsConstInt() is not int index)
                return UnknownValue.Instance;

            MultiValue result = TopValue;
            foreach (var value in arrayValue.AsEnumerable())
            {
                if (value is ArrayValue arr && arr.TryGetValueByIndex(index, out var elementValue))
                    result = _multiValueLattice.Meet(result, elementValue);
                else
                    return UnknownValue.Instance;
            }
            return result.Equals(TopValue) ? UnknownValue.Instance : result;
        }

        public override void HandleArrayElementWrite(MultiValue arrayValue, MultiValue indexValue, MultiValue valueToWrite, IOperation operation, bool merge)
        {
            int? index = indexValue.AsConstInt();
            foreach (var arraySingleValue in arrayValue.AsEnumerable())
            {
                if (arraySingleValue is ArrayValue arr)
                {
                    if (index == null)
                    {
                        // Reset the array to all unknowns - since we don't know which index is being assigned
                        arr.IndexValues.Clear();
                    }
                    else if (arr.IndexValues.TryGetValue(index.Value, out _) || arr.IndexValues.Count < MaxTrackedArrayValues)
                    {
                        var sanitizedValue = ArrayValue.SanitizeArrayElementValue(valueToWrite);
                        arr.IndexValues[index.Value] = merge
                            ? _multiValueLattice.Meet(arr.IndexValues[index.Value], sanitizedValue)
                            : sanitizedValue;
                    }
                }
                else if (arraySingleValue is ArrayOfAnnotatedSystemTypeValue arrayOfAnnotated)
                {
                    arrayOfAnnotated.MarkModified();
                }
            }
        }

        public override MultiValue HandleMethodCall(
            IMethodSymbol calledMethod,
            MultiValue instance,
            ImmutableArray<MultiValue> arguments,
            IOperation operation,
            in FeatureContext featureContext)
        {
            // For .ctors:
            // - The instance value is empty (TopValue) and that's a bit wrong.
            //   Technically this is an instance call and the instance is some valid value, we just don't know which
            //   but for example it does have a static type. For now this is OK since we don't need the information
            //   for anything yet.
            // - The return here is also technically problematic, the return value is an instance of a known type,
            //   but currently we return empty (since the .ctor is declared as returning void).
            //   Especially with DAM on type, this can lead to incorrectly analyzed code (as in unknown type which leads
            //   to noise). ILLink has the same problem currently: https://github.com/dotnet/linker/issues/1952

            HandleCall(operation, OwningSymbol, calledMethod, instance, arguments, Location.None, null, _multiValueLattice, out MultiValue methodReturnValue);

            // This will copy the values if necessary
            TrimAnalysisPatterns.Add(new TrimAnalysisMethodCallPattern(
                calledMethod,
                instance,
                arguments,
                operation,
                OwningSymbol,
                featureContext));

            ProcessGenericArgumentDataFlow(calledMethod, operation, featureContext);

            foreach (var argument in arguments)
            {
                foreach (var argumentValue in argument.AsEnumerable())
                {
                    if (argumentValue is ArrayValue arrayValue)
                        arrayValue.IndexValues.Clear();
                    else if (argumentValue is ArrayOfAnnotatedSystemTypeValue arrayOfAnnotated)
                        arrayOfAnnotated.MarkModified();
                }
            }

            return methodReturnValue;
        }

        internal static void HandleCall(
            IOperation operation,
            ISymbol owningSymbol,
            IMethodSymbol calledMethod,
            MultiValue instance,
            ImmutableArray<MultiValue> arguments,
            Location location,
            Action<Diagnostic>? reportDiagnostic,
            ValueSetLattice<SingleValue> multiValueLattice,
            out MultiValue methodReturnValue)
        {
            var handleCallAction = new HandleCallAction(location, owningSymbol, operation, multiValueLattice, reportDiagnostic);
            MethodProxy method = new(calledMethod);
            var intrinsicId = Intrinsics.GetIntrinsicIdForMethod(method);
            if (!handleCallAction.Invoke(method, instance, arguments, intrinsicId, out methodReturnValue))
                UnhandledIntrinsicHelper(intrinsicId);

            // Avoid crashing the analyzer in release builds
            [Conditional("DEBUG")]
            static void UnhandledIntrinsicHelper(IntrinsicId intrinsicId)
                => throw new NotImplementedException($"Unhandled intrinsic: {intrinsicId}");
        }

        public override void HandleReturnValue(MultiValue returnValue, IOperation operation, in FeatureContext featureContext)
        {
            // Return statements should only happen inside of method bodies.
            Debug.Assert(OwningSymbol is IMethodSymbol);
            if (OwningSymbol is not IMethodSymbol method)
                return;

            if (method.ReturnType.IsTypeInterestingForDataflow(isByRef: method.ReturnsByRef))
            {
                var returnParameter = new MethodReturnValue(method, isNewObj: false);

                TrimAnalysisPatterns.Add(
                    new TrimAnalysisAssignmentPattern(returnValue, returnParameter, operation, OwningSymbol, featureContext));
            }
        }

        public override void HandleReturnConditionValue(FeatureChecksValue returnConditionValue, IOperation operation)
        {
            // Return statements should only happen inside of method bodies.
            Debug.Assert(OwningSymbol is IMethodSymbol);
            if (OwningSymbol is not IMethodSymbol method)
                return;

            // FeatureGuard validation needs to happen only for property getters.
            // Include properties with setters here because they will get validated later.
            if (method.MethodKind != MethodKind.PropertyGet)
                return;

            IPropertySymbol propertySymbol = (IPropertySymbol)method.AssociatedSymbol!;
            var featureCheckAnnotations = propertySymbol.GetFeatureGuardAnnotations();

            // If there are no feature checks, there is nothing to validate.
            if (featureCheckAnnotations.IsEmpty())
                return;

            TrimAnalysisPatterns.Add(
                new FeatureCheckReturnValuePattern(returnConditionValue, featureCheckAnnotations, operation, propertySymbol));
        }

        public override MultiValue HandleDelegateCreation(IMethodSymbol method, IOperation operation, in FeatureContext featureContext)
        {
            TrimAnalysisPatterns.Add(new TrimAnalysisReflectionAccessPattern(
                method,
                operation,
                OwningSymbol,
                featureContext
            ));

            ProcessGenericArgumentDataFlow(method, operation, featureContext);

            return TopValue;
        }

        private void ProcessGenericArgumentDataFlow(IMethodSymbol method, IOperation operation, in FeatureContext featureContext)
        {
            // We only need to validate static methods and then all generic methods
            // Instance non-generic methods don't need validation because the creation of the instance
            // is the place where the validation will happen.
            if (!method.IsStatic && !method.IsGenericMethod && !method.IsConstructor())
                return;

            if (GenericArgumentDataFlow.RequiresGenericArgumentDataFlow(method))
            {
                TrimAnalysisPatterns.Add(new TrimAnalysisGenericInstantiationPattern(
                    method,
                    operation,
                    OwningSymbol,
                    featureContext));
            }
        }

        private void ProcessGenericArgumentDataFlow(IFieldSymbol field, IOperation operation, in FeatureContext featureContext)
        {
            // We only need to validate static field accesses, instance field accesses don't need generic parameter validation
            // because the create of the instance would do that instead.
            if (!field.IsStatic)
                return;

            if (GenericArgumentDataFlow.RequiresGenericArgumentDataFlow(field))
            {
                TrimAnalysisPatterns.Add(new TrimAnalysisGenericInstantiationPattern(
                    field,
                    operation,
                    OwningSymbol,
                    featureContext));
            }
        }

        private void ProcessGenericArgumentDataFlow(IPropertySymbol property, IOperation operation, in FeatureContext featureContext)
        {
            if (!property.IsStatic)
                return;

            if (GenericArgumentDataFlow.RequiresGenericArgumentDataFlow(property))
            {
                TrimAnalysisPatterns.Add(new TrimAnalysisGenericInstantiationPattern(
                    property,
                    operation,
                    OwningSymbol,
                    featureContext));
            }
        }

        private static bool TryGetConstantValue(IOperation operation, out MultiValue constValue)
        {
            if (operation.ConstantValue.HasValue)
            {
                object? constantValue = operation.ConstantValue.Value;
                if (constantValue == null)
                {
                    constValue = NullValue.Instance;
                    return true;
                }
                else if (operation.Type?.TypeKind == TypeKind.Enum && constantValue is int enumConstantValue)
                {
                    constValue = new ConstIntValue(enumConstantValue);
                    return true;
                }
                else
                {
                    switch (operation.Type?.SpecialType)
                    {
                        case SpecialType.System_String when constantValue is string stringConstantValue:
                            constValue = new KnownStringValue(stringConstantValue);
                            return true;
                        case SpecialType.System_Boolean when constantValue is bool boolConstantValue:
                            constValue = new ConstIntValue(boolConstantValue ? 1 : 0);
                            return true;
                        case SpecialType.System_SByte when constantValue is sbyte sbyteConstantValue:
                            constValue = new ConstIntValue(sbyteConstantValue);
                            return true;
                        case SpecialType.System_Byte when constantValue is byte byteConstantValue:
                            constValue = new ConstIntValue(byteConstantValue);
                            return true;
                        case SpecialType.System_Int16 when constantValue is short int16ConstantValue:
                            constValue = new ConstIntValue(int16ConstantValue);
                            return true;
                        case SpecialType.System_UInt16 when constantValue is ushort uint16ConstantValue:
                            constValue = new ConstIntValue(uint16ConstantValue);
                            return true;
                        case SpecialType.System_Int32 when constantValue is int int32ConstantValue:
                            constValue = new ConstIntValue(int32ConstantValue);
                            return true;
                        case SpecialType.System_UInt32 when constantValue is uint uint32ConstantValue:
                            constValue = new ConstIntValue((int)uint32ConstantValue);
                            return true;
                    }
                }
            }

            constValue = default;
            return false;
        }
    }
}
