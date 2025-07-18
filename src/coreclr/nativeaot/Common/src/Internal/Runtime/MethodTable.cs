// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Internal.NativeFormat;

using Debug = System.Diagnostics.Debug;

namespace Internal.Runtime
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjHeader
    {
        // Contents of the object header
        private IntPtr _objHeaderContents;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DispatchMap
    {
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct DispatchMapEntry
        {
            internal ushort _usInterfaceIndex;
            internal ushort _usInterfaceMethodSlot;
            internal ushort _usImplMethodSlot;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StaticDispatchMapEntry
        {
            // Do not put any other fields before this one. We need StaticDispatchMapEntry* be castable to DispatchMapEntry*.
            internal DispatchMapEntry _entry;
            internal ushort _usContextMapSource;
        }

        private ushort _standardEntryCount; // Implementations on the class
        private ushort _defaultEntryCount; // Default implementations
        private ushort _standardStaticEntryCount; // Implementations on the class (static virtuals)
        private ushort _defaultStaticEntryCount; // Default implementations (static virtuals)
        private DispatchMapEntry _dispatchMap; // at least one entry if any interfaces defined

        public uint NumStandardEntries
        {
            get
            {
                return _standardEntryCount;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _standardEntryCount = checked((ushort)value);
            }
#endif
        }

        public uint NumDefaultEntries
        {
            get
            {
                return _defaultEntryCount;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _defaultEntryCount = checked((ushort)value);
            }
#endif
        }

        public uint NumStandardStaticEntries
        {
            get
            {
                return _standardStaticEntryCount;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _standardStaticEntryCount = checked((ushort)value);
            }
#endif
        }

        public uint NumDefaultStaticEntries
        {
            get
            {
                return _defaultStaticEntryCount;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _defaultStaticEntryCount = checked((ushort)value);
            }
#endif
        }

        public int Size
        {
            get
            {
                return sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + sizeof(ushort)
                    + sizeof(DispatchMapEntry) * ((int)_standardEntryCount + (int)_defaultEntryCount)
                    + sizeof(StaticDispatchMapEntry) * ((int)_standardStaticEntryCount + (int)_defaultStaticEntryCount);
            }
        }

        public DispatchMapEntry* GetEntry(int index)
        {
            Debug.Assert(index <= _defaultEntryCount + _standardEntryCount);
            return (DispatchMapEntry*)Unsafe.AsPointer(ref Unsafe.Add(ref _dispatchMap, index));
        }

        public DispatchMapEntry* GetStaticEntry(int index)
        {
            Debug.Assert(index <= _defaultStaticEntryCount + _standardStaticEntryCount);
            return (DispatchMapEntry*)(((StaticDispatchMapEntry*)Unsafe.AsPointer(ref Unsafe.Add(ref _dispatchMap, _standardEntryCount + _defaultEntryCount))) + index);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe partial struct MethodTable
    {
#if TARGET_64BIT
        private const int POINTER_SIZE = 8;
        private const int PADDING = 1; // _numComponents is padded by one Int32 to make the first element pointer-aligned
#else
        private const int POINTER_SIZE = 4;
        private const int PADDING = 0;
#endif
        internal const int SZARRAY_BASE_SIZE = POINTER_SIZE + POINTER_SIZE + (1 + PADDING) * 4;

        [StructLayout(LayoutKind.Explicit)]
        private unsafe struct RelatedTypeUnion
        {
            // Kinds.CanonicalEEType
            [FieldOffset(0)]
            public MethodTable* _pBaseType;

            // Kinds.ArrayEEType
            [FieldOffset(0)]
            public MethodTable* _pRelatedParameterType;
        }

        /// <summary>
        /// Gets a value indicating whether the statically generated data structures use relative pointers.
        /// </summary>
        internal static bool SupportsRelativePointers
        {
            [Intrinsic]
            get
            {
                throw new NotImplementedException();
            }
        }

        [Intrinsic]
        internal static extern MethodTable* Of<T>();

        // upper ushort is used for Flags
        // lower ushort is used for
        // - component size for strings and arrays,
        // - type arg count for generic type definitions MethodTables,
        // - otherwise holds ExtendedFlags bits
        private uint _uFlags;
        private uint _uBaseSize;
        private RelatedTypeUnion _relatedType;
        private ushort _usNumVtableSlots;
        private ushort _usNumInterfaces;
        private uint _uHashCode;

        // vtable follows

        internal bool HasComponentSize
        {
            get
            {
                // return (_uFlags & (uint)EETypeFlags.HasComponentSizeFlag) != 0;
                return (int)_uFlags < 0;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                if (value)
                {
                    Debug.Assert(ExtendedFlags == 0);
                    _uFlags |= (uint)EETypeFlags.HasComponentSizeFlag;
                }
                else
                {
                    // we should not be un-setting this bit.
                    Debug.Assert(!HasComponentSize);
                }
            }
#endif
        }

        internal ushort ComponentSize
        {
            get
            {
                return HasComponentSize ? (ushort)_uFlags : (ushort)0;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(HasComponentSize);
                _uFlags |= (uint)value;
            }
#endif
        }

        internal ushort GenericParameterCount
        {
            get
            {
                Debug.Assert(IsGenericTypeDefinition);
                return (ushort)_uBaseSize;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsGenericTypeDefinition);
                _uBaseSize = value;
            }
#endif
        }

        internal uint Flags
        {
            get
            {
                return _uFlags;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uFlags = value;
            }
#endif
        }

        internal ushort ExtendedFlags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return HasComponentSize ? (ushort)0 : (ushort)_uFlags;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(!HasComponentSize);
                Debug.Assert(ExtendedFlags == 0);
                _uFlags |= (uint)value;
            }
#endif
        }

        internal uint RawBaseSize
        {
            get
            {
                return _uBaseSize;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uBaseSize = value;
            }
#endif
        }

        internal uint BaseSize
        {
            get
            {
                Debug.Assert(IsCanonical || IsArray);
                return _uBaseSize;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uBaseSize = value;
            }
#endif
        }

        internal ushort NumVtableSlots
        {
            get
            {
                return _usNumVtableSlots;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _usNumVtableSlots = value;
            }
#endif
        }

        internal ushort NumInterfaces
        {
            get
            {
                return _usNumInterfaces;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _usNumInterfaces = value;
            }
#endif
        }

        internal uint HashCode
        {
            get
            {
                return _uHashCode;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uHashCode = value;
            }
#endif
        }

        private EETypeKind Kind
        {
            get
            {
                return (EETypeKind)(_uFlags & (uint)EETypeFlags.EETypeKindMask);
            }
        }

        // Mark or determine that a type is generic and one or more of it's type parameters is co- or
        // contra-variant. This only applies to interface and delegate types.
        internal bool HasGenericVariance
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.GenericVarianceFlag) != 0;
            }
        }

        internal bool IsFinalizable
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.HasFinalizerFlag) != 0;
            }
        }

        internal bool IsNullable
        {
            get
            {
                return ElementType == EETypeElementType.Nullable;
            }
        }

        internal bool IsDefType
        {
            get
            {
                EETypeKind kind = Kind;
                return kind == EETypeKind.CanonicalEEType || kind == EETypeKind.GenericTypeDefEEType;
            }
        }

        internal bool IsCanonical
        {
            get
            {
                return Kind == EETypeKind.CanonicalEEType;
            }
        }

        internal bool IsString
        {
            get
            {
                // String is currently the only non-array type with a non-zero component size.
                return ComponentSize == StringComponentSize.Value && IsCanonical;
            }
        }

        internal bool IsArray
        {
            get
            {
                EETypeElementType elementType = ElementType;
                return elementType == EETypeElementType.Array || elementType == EETypeElementType.SzArray;
            }
        }


        internal int ArrayRank
        {
            get
            {
                Debug.Assert(this.IsArray);

                int boundsSize = (int)this.BaseSize - SZARRAY_BASE_SIZE;
                if (boundsSize > 0)
                {
                    // Multidim array case: Base size includes space for two Int32s
                    // (upper and lower bound) per each dimension of the array.
                    return (int)((uint)boundsSize / (uint)(2 * sizeof(int)));
                }
                return 1;
            }
        }

        // Returns rank of multi-dimensional array rank, 0 for sz arrays
        internal int MultiDimensionalArrayRank
        {
            get
            {
                Debug.Assert(this.IsArray);

                int boundsSize = (int)this.BaseSize - SZARRAY_BASE_SIZE;
                // Multidim array case: Base size includes space for two Int32s
                // (upper and lower bound) per each dimension of the array.
                return (int)((uint)boundsSize / (uint)(2 * sizeof(int)));
            }
        }

        internal bool IsSzArray
        {
            get
            {
                Debug.Assert(IsArray);
                return BaseSize == SZARRAY_BASE_SIZE;
            }
        }

        internal bool IsMultiDimensionalArray
        {
            get
            {
                Debug.Assert(HasComponentSize);
                // See comment on RawArrayData for details
                return BaseSize > (uint)(3 * sizeof(IntPtr));
            }
        }

        internal bool IsGeneric
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.IsGenericFlag) != 0;
            }
        }

        internal bool IsGenericTypeDefinition
        {
            get
            {
                return Kind == EETypeKind.GenericTypeDefEEType;
            }
        }

        internal MethodTable* GenericDefinition
        {
            get
            {
                Debug.Assert(IsGeneric);

                uint offset = GetFieldOffset(EETypeField.ETF_GenericDefinition);

                if (IsDynamicType || !SupportsRelativePointers)
                    return GetField<Pointer<MethodTable>>(offset).Value;

                return GetField<RelativePointer<MethodTable>>(offset).Value;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsGeneric && IsDynamicType);
                GetField<IntPtr>(EETypeField.ETF_GenericDefinition) = (IntPtr)value;
            }
#endif
        }

#if TYPE_LOADER_IMPLEMENTATION
        internal static int GetGenericCompositionSize(int numArguments)
        {
            return numArguments * IntPtr.Size;
        }

        internal void SetGenericComposition(IntPtr data)
        {
            Debug.Assert(IsGeneric && IsDynamicType);
            GetField<IntPtr>(EETypeField.ETF_GenericComposition) = data;
        }
#endif

        internal uint GenericArity
        {
            get
            {
                Debug.Assert(IsGeneric);
                return GenericDefinition->GenericParameterCount;
            }
        }

        internal MethodTableList GenericArguments
        {
            get
            {
                Debug.Assert(IsGeneric);

                void* pField = (byte*)Unsafe.AsPointer(ref this) + GetFieldOffset(EETypeField.ETF_GenericComposition);
                uint arity = GenericArity;

                // If arity is 1, the field value is the component. For arity > 1, components are stored out-of-line
                // and are shared.
                if (IsDynamicType || !SupportsRelativePointers)
                {
                    // This is a full pointer [that points to a list of full pointers]
                    MethodTable* pListStart = arity == 1 ? (MethodTable*)pField : *(MethodTable**)pField;
                    return new MethodTableList(pListStart);
                }
                else
                {
                    // This is a relative pointer [that points to a list of relative pointers]
                    RelativePointer<MethodTable>* pListStart = arity == 1 ?
                        (RelativePointer<MethodTable>*)pField : (RelativePointer<MethodTable>*)((RelativePointer*)pField)->Value;
                    return new MethodTableList(pListStart);
                }
            }
        }

        internal GenericVariance* GenericVariance
        {
            get
            {
                Debug.Assert(IsGeneric || IsGenericTypeDefinition);

                if (!HasGenericVariance)
                    return null;

                if (IsGeneric)
                    return GenericDefinition->GenericVariance;

                uint offset = GetFieldOffset(EETypeField.ETF_GenericComposition);

                if (IsDynamicType || !SupportsRelativePointers)
                    return GetField<Pointer<GenericVariance>>(offset).Value;

                return GetField<RelativePointer<GenericVariance>>(offset).Value;
            }
        }

        internal bool IsPointer
        {
            get
            {
                return ElementType == EETypeElementType.Pointer;
            }
        }

        internal bool IsByRef
        {
            get
            {
                return ElementType == EETypeElementType.ByRef;
            }
        }

        internal bool IsInterface
        {
            get
            {
                return ElementType == EETypeElementType.Interface;
            }
        }

        internal bool IsByRefLike
        {
            get
            {
                return IsValueType && (_uFlags & (uint)EETypeFlagsEx.IsByRefLikeFlag) != 0;
            }
        }

        internal bool IsDynamicType
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.IsDynamicTypeFlag) != 0;
            }
        }

        internal bool IsParameterizedType
        {
            get
            {
                return Kind == EETypeKind.ParameterizedEEType;
            }
        }

        internal bool IsFunctionPointer
        {
            get
            {
                return Kind == EETypeKind.FunctionPointerEEType;
            }
        }

        // The parameterized type shape defines the particular form of parameterized type that
        // is being represented.
        // Currently, the meaning is a shape of 0 indicates that this is a Pointer,
        // shape of 1 indicates a ByRef, and >=SZARRAY_BASE_SIZE indicates that this is an array.
        // Two types are not equivalent if their shapes do not exactly match.
        internal uint ParameterizedTypeShape
        {
            get
            {
                Debug.Assert(IsParameterizedType);
                return _uBaseSize;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uBaseSize = value;
            }
#endif
        }

        internal uint NumFunctionPointerParameters
        {
            get
            {
                Debug.Assert(IsFunctionPointer);
                return _uBaseSize & ~FunctionPointerFlags.FlagsMask;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsFunctionPointer);
                _uBaseSize = value | (_uBaseSize & FunctionPointerFlags.FlagsMask);
            }
#endif
        }

        internal bool IsUnmanagedFunctionPointer
        {
            get
            {
                Debug.Assert(IsFunctionPointer);
                return (_uBaseSize & FunctionPointerFlags.IsUnmanaged) != 0;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsFunctionPointer);
                if (value)
                    _uBaseSize |= FunctionPointerFlags.IsUnmanaged;
                else
                    _uBaseSize &= ~FunctionPointerFlags.IsUnmanaged;
            }
#endif
        }

        internal MethodTableList FunctionPointerParameters
        {
            get
            {
                void* pStart = (byte*)Unsafe.AsPointer(ref this) + GetFieldOffset(EETypeField.ETF_FunctionPointerParameters);
                if (IsDynamicType || !SupportsRelativePointers)
                    return new MethodTableList((MethodTable*)pStart);
                return new MethodTableList((RelativePointer<MethodTable>*)pStart);
            }
        }

        internal MethodTable* FunctionPointerReturnType
        {
            get
            {
                Debug.Assert(IsFunctionPointer);
                return _relatedType._pRelatedParameterType;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType && IsFunctionPointer);
                _relatedType._pRelatedParameterType = value;
            }
#endif
        }

        internal bool RequiresAlign8
        {
            get
            {
                // NOTE: Does not work for types with HasComponentSize, ie. arrays and strings.
                // Since this is called early through RhNewObject we cannot use regular Debug.Assert
                // here to enforce the assumption.
#if DEBUG
                if (HasComponentSize)
                    Debug.Fail("RequiresAlign8 called for array or string");
#endif
                return (_uFlags & (uint)EETypeFlagsEx.RequiresAlign8Flag) != 0;
            }
        }

        internal bool IsIDynamicInterfaceCastable
        {
            get
            {
                return ((ExtendedFlags & (ushort)EETypeFlagsEx.IDynamicInterfaceCastableFlag) != 0);
            }
        }

        internal bool IsValueType
        {
            get
            {
                return ElementType < EETypeElementType.Class;
            }
        }

        // Warning! UNLIKE the similarly named Reflection api, this method also returns "true" for Enums.
        internal bool IsPrimitive
        {
            get
            {
                return ElementType < EETypeElementType.ValueType;
            }
        }

        internal bool HasSealedVTableEntries
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.HasSealedVTableEntriesFlag) != 0;
            }
        }

        internal bool ContainsGCPointers
        {
            get
            {
                return ((_uFlags & (uint)EETypeFlags.HasPointersFlag) != 0);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                if (value)
                {
                    _uFlags |= (uint)EETypeFlags.HasPointersFlag;
                }
                else
                {
                    _uFlags &= (uint)~EETypeFlags.HasPointersFlag;
                }
            }
#endif
        }

        internal bool IsTrackedReferenceWithFinalizer
        {
            get
            {
                return (ExtendedFlags & (ushort)EETypeFlagsEx.IsTrackedReferenceWithFinalizerFlag) != 0;
            }
        }

        internal uint ValueTypeFieldPadding
        {
            get
            {
                Debug.Assert(IsValueType);
                return (_uFlags & (uint)EETypeFlagsEx.ValueTypeFieldPaddingMask) >> ValueTypeFieldPaddingConsts.Shift;
            }
        }

        internal uint ValueTypeSize
        {
            get
            {
                Debug.Assert(IsValueType);
                // BaseSize returns the GC size including space for the sync block index field, the MethodTable* and
                // padding for GC heap alignment. Must subtract all of these to get the size used for locals, array
                // elements or fields of another type.
                return BaseSize - ((uint)sizeof(ObjHeader) + (uint)sizeof(MethodTable*) + ValueTypeFieldPadding);
            }
        }

        internal MethodTable** InterfaceMap
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // interface info table starts after the vtable and has _usNumInterfaces entries
                return (MethodTable**)((byte*)Unsafe.AsPointer(ref this) + sizeof(MethodTable) + sizeof(void*) * _usNumVtableSlots);
            }
        }

        internal bool HasDispatchMap
        {
            get
            {
                return (_uFlags & (uint)EETypeFlags.HasDispatchMap) != 0;
            }
        }

        internal DispatchMap* DispatchMap
        {
            get
            {
                if (!HasDispatchMap)
                    return null;

                uint offset = GetFieldOffset(EETypeField.ETF_DispatchMap);

                if (IsDynamicType || !SupportsRelativePointers)
                    return GetField<Pointer<DispatchMap>>(offset).Value;

                return GetField<RelativePointer<DispatchMap>>(offset).Value;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType && HasDispatchMap);
                GetField<IntPtr>(EETypeField.ETF_DispatchMap) = (IntPtr)value;
            }
#endif
        }

        // Get the address of the finalizer method for finalizable types.
        internal IntPtr FinalizerCode
        {
            get
            {
                Debug.Assert(IsFinalizable);

                uint offset = GetFieldOffset(EETypeField.ETF_Finalizer);

                if (IsDynamicType || !SupportsRelativePointers)
                    return GetField<Pointer>(offset).Value;

                return GetField<RelativePointer>(offset).Value;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType && IsFinalizable);
                GetField<IntPtr>(EETypeField.ETF_Finalizer) = value;
            }
#endif
        }

        internal MethodTable* BaseType
        {
            get
            {
                if (!IsCanonical)
                {
                    if (IsArray)
                        return GetArrayEEType();
                    else
                        return null;
                }

                return _relatedType._pBaseType;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType);
                Debug.Assert(!IsParameterizedType);
                Debug.Assert(!IsFunctionPointer);
                Debug.Assert(IsCanonical);
                _relatedType._pBaseType = value;
            }
#endif
        }

        internal MethodTable* NonArrayBaseType
        {
            get
            {
                Debug.Assert(!IsArray, "array type not supported in NonArrayBaseType");
                Debug.Assert(IsCanonical || IsGenericTypeDefinition, "we expect type definitions here");
                Debug.Assert(!IsGenericTypeDefinition || _relatedType._pBaseType == null, "callers assume this would be null for a generic definition");
                return _relatedType._pBaseType;
            }
        }

        internal MethodTable* NullableType
        {
            get
            {
                Debug.Assert(IsNullable);
                Debug.Assert(GenericArity == 1);
                return GenericArguments[0];
            }
        }

        /// <summary>
        /// Gets the offset of the value embedded in a Nullable&lt;T&gt;.
        /// </summary>
        internal byte NullableValueOffset
        {
            get
            {
                Debug.Assert(IsNullable);
                int log2valueoffset = (int)(_uFlags & (ushort)EETypeFlagsEx.NullableValueOffsetMask) >> NullableValueOffsetConsts.Shift;
                return (byte)(1 << log2valueoffset);
            }
        }

        internal MethodTable* RelatedParameterType
        {
            get
            {
                Debug.Assert(IsParameterizedType);
                return _relatedType._pRelatedParameterType;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType && IsParameterizedType);
                _relatedType._pRelatedParameterType = value;
            }
#endif
        }

        internal unsafe IntPtr* GetVTableStartAddress()
        {
            // EETypes are always in unmanaged memory, so 'leaking' the 'fixed pointer' is safe.
            return (IntPtr*)((byte*)Unsafe.AsPointer(ref this) + sizeof(MethodTable));
        }

        private static IntPtr FollowRelativePointer(int* pDist)
        {
            int dist = *pDist;
            IntPtr result = (IntPtr)((byte*)pDist + dist);
            return result;
        }

#if TYPE_LOADER_IMPLEMENTATION
        internal
#else
        private
#endif
        void* GetSealedVirtualTable()
        {
            Debug.Assert(HasSealedVTableEntries);

            uint cbSealedVirtualSlotsTypeOffset = GetFieldOffset(EETypeField.ETF_SealedVirtualSlots);
            byte* pThis = (byte*)Unsafe.AsPointer(ref this);
            if (IsDynamicType || !SupportsRelativePointers)
            {
                return *(void**)(pThis + cbSealedVirtualSlotsTypeOffset);
            }
            else
            {
                return (void*)FollowRelativePointer((int*)(pThis + cbSealedVirtualSlotsTypeOffset));
            }
        }

        internal IntPtr GetSealedVirtualSlot(ushort slotNumber)
        {
            void* pSealedVtable = GetSealedVirtualTable();
            if (!SupportsRelativePointers)
            {
                return ((IntPtr*)pSealedVtable)[slotNumber];
            }
            else
            {
                return FollowRelativePointer(&((int*)pSealedVtable)[slotNumber]);
            }
        }

        internal MethodTable* DynamicTemplateType
        {
            get
            {
                Debug.Assert(IsDynamicType);
                return GetField<Pointer<MethodTable>>(EETypeField.ETF_DynamicTemplateType).Value;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType);
                GetField<IntPtr>(EETypeField.ETF_DynamicTemplateType) = (IntPtr)value;
            }
#endif
        }

        internal bool IsDynamicTypeWithCctor
        {
            get
            {
                return (DynamicTypeFlags & DynamicTypeFlags.HasLazyCctor) != 0;
            }
        }

        internal IntPtr DynamicGcStaticsData
        {
            get
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasGCStatics) != 0);
                return GetField<IntPtr>(EETypeField.ETF_DynamicGcStatics);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasGCStatics) != 0);
                GetField<IntPtr>(EETypeField.ETF_DynamicGcStatics) = value;
            }
#endif
        }

        internal IntPtr DynamicNonGcStaticsData
        {
            get
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasNonGCStatics) != 0);
                return GetField<IntPtr>(EETypeField.ETF_DynamicNonGcStatics);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasNonGCStatics) != 0);
                GetField<IntPtr>(EETypeField.ETF_DynamicNonGcStatics) = value;
            }
#endif
        }

        internal IntPtr DynamicThreadStaticsIndex
        {
            get
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasThreadStatics) != 0);
                return GetField<IntPtr>(EETypeField.ETF_DynamicThreadStaticOffset);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert((DynamicTypeFlags & DynamicTypeFlags.HasThreadStatics) != 0);
                GetField<IntPtr>(EETypeField.ETF_DynamicThreadStaticOffset) = value;
            }
#endif
        }

        internal TypeManagerHandle TypeManager
        {
            get
            {
                uint offset = GetFieldOffset(EETypeField.ETF_TypeManagerIndirection);

                IntPtr typeManagerIndirection;
                if (IsDynamicType || !SupportsRelativePointers)
                    typeManagerIndirection = GetField<Pointer>(offset).Value;
                else
                    typeManagerIndirection = GetField<RelativePointer>(offset).Value;

                return *(TypeManagerHandle*)typeManagerIndirection;
            }
        }
#if TYPE_LOADER_IMPLEMENTATION
        internal IntPtr PointerToTypeManager
        {
            get
            {
                uint offset = GetFieldOffset(EETypeField.ETF_TypeManagerIndirection);

                if (IsDynamicType || !SupportsRelativePointers)
                    return GetField<Pointer>(offset).Value;

                return GetField<RelativePointer>(offset).Value;
            }
            set
            {
                Debug.Assert(IsDynamicType);
                GetField<IntPtr>(EETypeField.ETF_TypeManagerIndirection) = value;
            }
        }
#endif

        /// <summary>
        /// Gets a pointer to a segment of writable memory associated with this MethodTable.
        /// The purpose of the segment is controlled by the class library. The runtime doesn't
        /// use this memory for any purpose.
        /// </summary>
        internal void* WritableData
        {
            get
            {
                uint offset = GetFieldOffset(EETypeField.ETF_WritableData);

                if (!IsDynamicType && SupportsRelativePointers)
                    return (void*)GetField<RelativePointer>(offset).Value;
                else
                    return (void*)GetField<Pointer>(offset).Value;
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType);
                GetField<IntPtr>(EETypeField.ETF_WritableData) = (IntPtr)value;
            }
#endif
        }

        internal DynamicTypeFlags DynamicTypeFlags
        {
            get
            {
                Debug.Assert(IsDynamicType);
                return (DynamicTypeFlags)GetField<nint>(EETypeField.ETF_DynamicTypeFlags);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(IsDynamicType);
                GetField<nint>(EETypeField.ETF_DynamicTypeFlags) = (nint)value;
            }
#endif
        }

        internal EETypeElementType ElementType
        {
            get
            {
                return (EETypeElementType)((_uFlags >> (byte)EETypeFlags.ElementTypeShift) &
                    ((uint)EETypeFlags.ElementTypeMask >> (byte)EETypeFlags.ElementTypeShift));
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                _uFlags = (_uFlags & ~(uint)EETypeFlags.ElementTypeMask) | ((uint)value << (byte)EETypeFlags.ElementTypeShift);
            }
#endif
        }

        // This method is always called with a known constant and there's a lot of benefit in inlining it.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetFieldOffset(EETypeField eField)
        {
            // First part of MethodTable consists of the fixed portion followed by the vtable.
            uint cbOffset = (uint)(sizeof(MethodTable) + (IntPtr.Size * _usNumVtableSlots));

            // Followed by list of implemented interfaces
            cbOffset += (uint)(sizeof(MethodTable*) * NumInterfaces);

            uint relativeOrFullPointerOffset = (IsDynamicType || !SupportsRelativePointers ? (uint)IntPtr.Size : 4);

            // Followed by the type manager indirection cell.
            if (eField == EETypeField.ETF_TypeManagerIndirection)
            {
                return cbOffset;
            }
            cbOffset += relativeOrFullPointerOffset;

            // Followed by writable data.
            if (eField == EETypeField.ETF_WritableData)
            {
                return cbOffset;
            }
            cbOffset += relativeOrFullPointerOffset;

            // Followed by pointer to the dispatch map
            if (eField == EETypeField.ETF_DispatchMap)
            {
                Debug.Assert(HasDispatchMap);
                return cbOffset;
            }
            if (HasDispatchMap)
                cbOffset += relativeOrFullPointerOffset;

            // Followed by the pointer to the finalizer method.
            if (eField == EETypeField.ETF_Finalizer)
            {
                Debug.Assert(IsFinalizable);
                return cbOffset;
            }
            if (IsFinalizable)
                cbOffset += relativeOrFullPointerOffset;

            // Followed by the pointer to the sealed virtual slots
            if (eField == EETypeField.ETF_SealedVirtualSlots)
                return cbOffset;

            // in the case of sealed vtable entries on static types, we have a UInt sized relative pointer
            if (HasSealedVTableEntries)
                cbOffset += relativeOrFullPointerOffset;

            if (eField == EETypeField.ETF_GenericDefinition)
            {
                Debug.Assert(IsGeneric);
                return cbOffset;
            }
            if (IsGeneric)
            {
                cbOffset += relativeOrFullPointerOffset;
            }

            if (eField == EETypeField.ETF_GenericComposition)
            {
                Debug.Assert(IsGeneric || (IsGenericTypeDefinition && HasGenericVariance));
                return cbOffset;
            }
            if (IsGeneric || (IsGenericTypeDefinition && HasGenericVariance))
            {
                cbOffset += relativeOrFullPointerOffset;
            }

            if (eField == EETypeField.ETF_FunctionPointerParameters)
            {
                Debug.Assert(IsFunctionPointer);
                return cbOffset;
            }
            if (IsFunctionPointer)
            {
                cbOffset += NumFunctionPointerParameters * relativeOrFullPointerOffset;
            }

            if (eField == EETypeField.ETF_DynamicTemplateType)
            {
                Debug.Assert(IsDynamicType);
                return cbOffset;
            }
            if (IsDynamicType)
                cbOffset += (uint)IntPtr.Size;

            DynamicTypeFlags dynamicTypeFlags = 0;
            if (eField == EETypeField.ETF_DynamicTypeFlags)
            {
                Debug.Assert(IsDynamicType);
                return cbOffset;
            }
            if (IsDynamicType)
            {
                dynamicTypeFlags = (DynamicTypeFlags)GetField<nint>(cbOffset);
                cbOffset += (uint)IntPtr.Size;
            }

            if (eField == EETypeField.ETF_DynamicGcStatics)
            {
                Debug.Assert((dynamicTypeFlags & DynamicTypeFlags.HasGCStatics) != 0);
                return cbOffset;
            }
            if ((dynamicTypeFlags & DynamicTypeFlags.HasGCStatics) != 0)
                cbOffset += (uint)IntPtr.Size;

            if (eField == EETypeField.ETF_DynamicNonGcStatics)
            {
                Debug.Assert((dynamicTypeFlags & DynamicTypeFlags.HasNonGCStatics) != 0);
                return cbOffset;
            }
            if ((dynamicTypeFlags & DynamicTypeFlags.HasNonGCStatics) != 0)
                cbOffset += (uint)IntPtr.Size;

            if (eField == EETypeField.ETF_DynamicThreadStaticOffset)
            {
                Debug.Assert((dynamicTypeFlags & DynamicTypeFlags.HasThreadStatics) != 0);
                return cbOffset;
            }

            Debug.Fail("Unknown MethodTable field type");
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetField<T>(EETypeField eField)
        {
            return ref Unsafe.As<byte, T>(ref *((byte*)Unsafe.AsPointer(ref this) + GetFieldOffset(eField)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetField<T>(uint offset)
        {
            return ref Unsafe.As<byte, T>(ref *((byte*)Unsafe.AsPointer(ref this) + offset));
        }

#if TYPE_LOADER_IMPLEMENTATION
        internal static uint GetSizeofEEType(
            ushort cVirtuals,
            ushort cInterfaces,
            bool fHasDispatchMap,
            bool fHasFinalizer,
            bool fHasSealedVirtuals,
            bool fHasGenericInfo,
            int cFunctionPointerTypeParameters,
            bool fHasNonGcStatics,
            bool fHasGcStatics,
            bool fHasThreadStatics)
        {
            return (uint)(sizeof(MethodTable) +
                (IntPtr.Size * cVirtuals) +
                (sizeof(MethodTable*) * cInterfaces) +
                sizeof(IntPtr) + // TypeManager
                sizeof(IntPtr) + // WritableData
                (fHasDispatchMap ? sizeof(UIntPtr) : 0) +
                (fHasFinalizer ? sizeof(UIntPtr) : 0) +
                (fHasSealedVirtuals ? sizeof(IntPtr) : 0) +
                cFunctionPointerTypeParameters * sizeof(IntPtr) +
                (fHasGenericInfo ? sizeof(IntPtr) * 2 : 0) + // pointers to GenericDefinition and GenericComposition
                sizeof(IntPtr) + // dynamic type flags
                (fHasNonGcStatics ? sizeof(IntPtr) : 0) + // pointer to data
                (fHasGcStatics ? sizeof(IntPtr) : 0) +  // pointer to data
                (fHasThreadStatics ? sizeof(IntPtr) : 0)); // threadstatic index cell
        }
#endif
    }

    // Wrapper around pointers
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Pointer
    {
        private readonly IntPtr _value;

        public IntPtr Value => _value;
    }

    // Wrapper around pointers
    [StructLayout(LayoutKind.Sequential)]
    internal readonly unsafe struct Pointer<T> where T : unmanaged
    {
        private readonly T* _value;

        public T* Value => _value;
    }

    // Wrapper around relative pointers
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct RelativePointer
    {
        private readonly int _value;

        public unsafe IntPtr Value => (IntPtr)((byte*)Unsafe.AsPointer(in _value) + _value);
    }

    // Wrapper around relative pointers
    [StructLayout(LayoutKind.Sequential)]
    internal readonly unsafe struct RelativePointer<T> where T : unmanaged
    {
        private readonly int _value;

        public T* Value => (T*)((byte*)Unsafe.AsPointer(in _value) + _value);
    }

    // Abstracts a list of MethodTable pointers that could either be relative
    // pointers or full pointers. We store the IsRelative bit in the lowest
    // bit so this assumes the list is at least 2 byte aligned.
    internal readonly unsafe struct MethodTableList
    {
        private const int IsRelative = 1;

        private readonly void* _pFirst;

        public MethodTableList(MethodTable* pFirst)
        {
            // If the first element is not aligned, we don't have the spare bit we need
            Debug.Assert(((nint)pFirst & IsRelative) == 0);
            _pFirst = pFirst;
        }

        public MethodTableList(RelativePointer<MethodTable>* pFirst)
        {
            // If the first element is not aligned, we don't have the spare bit we need
            Debug.Assert(((nint)pFirst & IsRelative) == 0);
            _pFirst = (void*)((nint)pFirst | IsRelative);
        }

        public MethodTable* this[int index]
        {
            get
            {
                if (((nint)_pFirst & IsRelative) != 0)
                    return (((RelativePointer<MethodTable>*)((nint)_pFirst - IsRelative)) + index)->Value;

                return *((MethodTable**)_pFirst + index);
            }
#if TYPE_LOADER_IMPLEMENTATION
            set
            {
                Debug.Assert(((nint)_pFirst & IsRelative) == 0);
                *((MethodTable**)_pFirst + index) = value;
            }
#endif
        }
    }
}
