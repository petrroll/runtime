// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

using ILCompiler;

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace Internal.IL.Stubs
{
    /// <summary>
    /// Synthetic method override of "int ValueType.__GetFieldHelper(Int32, out MethodTable*)". This method is injected
    /// into all value types that cannot have their Equals(object) and GetHashCode() methods operate on individual
    /// bytes. The purpose of the override is to provide access to the value types' fields and their types.
    /// </summary>
    public sealed partial class ValueTypeGetFieldHelperMethodOverride : SpecializableILStubMethod
    {
        private MetadataType _owningType;
        private MethodSignature _signature;

        internal ValueTypeGetFieldHelperMethodOverride(MetadataType owningType)
        {
            _owningType = owningType;
        }

        public override TypeSystemContext Context
        {
            get
            {
                return _owningType.Context;
            }
        }

        public override TypeDesc OwningType
        {
            get
            {
                return _owningType;
            }
        }

        public override MethodSignature Signature
        {
            get
            {
                if (_signature == null)
                {
                    TypeSystemContext context = _owningType.Context;
                    TypeDesc int32Type = context.GetWellKnownType(WellKnownType.Int32);
                    TypeDesc eeTypePtrType = context.SystemModule.GetKnownType("Internal.Runtime", "MethodTable").MakePointerType();

                    _signature = new MethodSignature(0, 0, int32Type, [ int32Type, eeTypePtrType.MakeByRefType() ]);
                }

                return _signature;
            }
        }

        public override MethodIL EmitIL()
        {
            return EmitILCommon(null);
        }

        public override MethodIL EmitIL(MethodDesc specializedMethod)
        {
            Debug.Assert(specializedMethod.GetTypicalMethodDefinition() == this);
            return new InstantiatedMethodIL(specializedMethod, EmitILCommon(specializedMethod));
        }

        private MethodIL EmitILCommon(MethodDesc contextMethod)
        {
            var owningType = (MetadataType)_owningType.InstantiateAsOpen();

            ILEmitter emitter = new ILEmitter();

            // Types marked as InlineArray aren't supported by
            // the built-in Equals() or GetHashCode().
            if (owningType.IsInlineArray)
            {
                var stream = emitter.NewCodeStream();
                MethodDesc thrower = Context.GetHelperEntryPoint("ThrowHelpers", "ThrowNotSupportedInlineArrayEqualsGetHashCode");
                stream.EmitCallThrowHelper(emitter, thrower);
                return emitter.Link(this);
            }

            TypeDesc methodTableType = Context.SystemModule.GetKnownType("Internal.Runtime", "MethodTable");
            MethodDesc methodTableOfMethod = methodTableType.GetKnownMethod("Of", null);

            ILToken rawDataToken = owningType.IsValueType ? default :
                emitter.NewToken(Context.SystemModule.GetKnownType("System.Runtime.CompilerServices", "RawData").GetKnownField("Data"));

            var switchStream = emitter.NewCodeStream();
            var getFieldStream = emitter.NewCodeStream();

            ArrayBuilder<ILCodeLabel> fieldGetters = default(ArrayBuilder<ILCodeLabel>);
            foreach (FieldDesc field in owningType.GetFields())
            {
                if (field.IsStatic)
                    continue;

                ILCodeLabel label = emitter.NewCodeLabel();
                fieldGetters.Add(label);

                getFieldStream.EmitLabel(label);
                getFieldStream.EmitLdArg(2);

                // We need something we can instantiate MethodTable.Of over. Also, the classlib
                // code doesn't handle pointers.
                TypeDesc boxableFieldType = field.FieldType;
                if (boxableFieldType.IsPointer || boxableFieldType.IsFunctionPointer)
                    boxableFieldType = Context.GetWellKnownType(WellKnownType.IntPtr);

                // We're trying to do some optimizations below that can benefit from knowing the concrete
                // type after substitutions.
                TypeDesc fieldTypeForOptimizationChecks = boxableFieldType.IsSignatureVariable
                    ? boxableFieldType.InstantiateSignature(contextMethod.OwningType.Instantiation, default)
                    : boxableFieldType;

                // The fact that the type is a reference type is sufficient for the callers.
                // Don't unnecessarily create a MethodTable for the field type.
                if (!fieldTypeForOptimizationChecks.IsValueType)
                    boxableFieldType = Context.GetWellKnownType(WellKnownType.Object);

                // If this is an enum, it's okay to Equals/GetHashCode the underlying type.
                // Don't unnecessarily create a MethodTable for the enum.
                if (fieldTypeForOptimizationChecks.IsEnum)
                    boxableFieldType = fieldTypeForOptimizationChecks.UnderlyingType;

                MethodDesc mtOfFieldMethod = methodTableOfMethod.MakeInstantiatedMethod(boxableFieldType);
                getFieldStream.Emit(ILOpcode.call, emitter.NewToken(mtOfFieldMethod));

                getFieldStream.Emit(ILOpcode.stind_i);

                getFieldStream.EmitLdArg(0);
                getFieldStream.Emit(ILOpcode.ldflda, emitter.NewToken(field));

                getFieldStream.EmitLdArg(0);

                // If this is a reference type, we subtract from the first field. Otherwise subtract from `ref this`.
                if (!owningType.IsValueType)
                    getFieldStream.Emit(ILOpcode.ldflda, rawDataToken);

                getFieldStream.Emit(ILOpcode.sub);

                getFieldStream.Emit(ILOpcode.ret);
            }

            if (fieldGetters.Count > 0)
            {
                switchStream.EmitLdArg(1);
                switchStream.EmitSwitch(fieldGetters.ToArray());
            }

            if (!owningType.IsValueType
                && MetadataVirtualMethodAlgorithm.FindSlotDefiningMethodForVirtualMethod(this) is MethodDesc slotMethod
                && owningType.BaseType.FindVirtualFunctionTargetMethodOnObjectType(slotMethod) is MethodDesc baseMethod
                && slotMethod != baseMethod)
            {
                // On reference types, we recurse into base implementation too, handling both the case of asking
                // for number of fields (add number of fields on the current class before returning), and
                // handling field indices higher than what we handle (subtract number of fields handled here first).
                switchStream.EmitLdArg(0);
                switchStream.EmitLdArg(1);
                switchStream.EmitLdc(fieldGetters.Count);
                switchStream.Emit(ILOpcode.sub);
                switchStream.EmitLdArg(2);
                switchStream.Emit(ILOpcode.call, emitter.NewToken(baseMethod));
                switchStream.EmitLdArg(1);
                switchStream.EmitLdc(0);
                ILCodeLabel lGotBaseFieldAddress = emitter.NewCodeLabel();
                switchStream.Emit(ILOpcode.bge, lGotBaseFieldAddress);
                switchStream.EmitLdc(fieldGetters.Count);
                ILCodeLabel lGotNumFieldsInBase = emitter.NewCodeLabel();
                switchStream.Emit(ILOpcode.br, lGotNumFieldsInBase);
                switchStream.EmitLabel(lGotBaseFieldAddress);
                switchStream.EmitLdc(0);
                switchStream.EmitLabel(lGotNumFieldsInBase);
                switchStream.Emit(ILOpcode.add);
                switchStream.Emit(ILOpcode.ret);
            }
            else
            {
                switchStream.EmitLdc(fieldGetters.Count);
                switchStream.Emit(ILOpcode.ret);
            }

            return emitter.Link(this);
        }

        public override Instantiation Instantiation
        {
            get
            {
                return Instantiation.Empty;
            }
        }

        public override bool IsVirtual
        {
            get
            {
                return true;
            }
        }

        internal const string MetadataName = "__GetFieldHelper";

        public override string Name
        {
            get
            {
                return MetadataName;
            }
        }

        public override string DiagnosticName
        {
            get
            {
                return MetadataName;
            }
        }
    }
}
