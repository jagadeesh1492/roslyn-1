﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Collections;

namespace Microsoft.CodeAnalysis.CodeGen
{
    /// <summary>
    /// TypeDefinition that represents &lt;PrivateImplementationDetails&gt; class.
    /// The main purpose of this class so far is to contain mapped fields and their types.
    /// </summary>
    internal sealed class PrivateImplementationDetails : DefaultTypeDef, Cci.INamespaceTypeDefinition
    {
        // Note: Dev11 uses the source method token as the prefix, rather than a fixed token
        // value, and data field offsets are unique within the method, not across all methods.
        private const string MemberNamePrefix = "$$method0x6000001-";
        internal const string SynthesizedStringHashFunctionName = MemberNamePrefix + "ComputeStringHash";

        private readonly Cci.IModule _module;                     //parent unit
        private readonly Cci.ITypeReference _systemObject;        //base type
        private readonly Cci.ITypeReference _systemValueType;     //base for nested structs

        private readonly Cci.ITypeReference _systemInt8Type;         //for metadata init of short arrays
        private readonly Cci.ITypeReference _systemInt16Type;        //for metadata init of short arrays
        private readonly Cci.ITypeReference _systemInt32Type;        //for metadata init of short arrays
        private readonly Cci.ITypeReference _systemInt64Type;        //for metadata init of short arrays

        private readonly Cci.ICustomAttribute _compilerGeneratedAttribute;

        private readonly string _name;

        // Once frozen the collections of fields, methods and types are immutable.
        private int _frozen;

        // fields mapped to metadata blocks
        private ImmutableArray<MappedField> _orderedMappedFields;
        private readonly ConcurrentDictionary<ImmutableArray<byte>, MappedField> _mappedFields =
            new ConcurrentDictionary<ImmutableArray<byte>, MappedField>(ByteSequenceComparer.Instance);

        // synthesized methods
        private ImmutableArray<Cci.IMethodDefinition> _orderedSynthesizedMethods;
        private readonly ConcurrentDictionary<string, Cci.IMethodDefinition> _synthesizedMethods =
            new ConcurrentDictionary<string, Cci.IMethodDefinition>();

        // field types for different block sizes.
        private ImmutableArray<Cci.ITypeReference> _orderedProxyTypes;
        private readonly ConcurrentDictionary<uint, Cci.ITypeReference> _proxyTypes = new ConcurrentDictionary<uint, Cci.ITypeReference>();

        internal PrivateImplementationDetails(
            Cci.IModule module,
            int submissionSlotIndex,
            Cci.ITypeReference systemObject,
            Cci.ITypeReference systemValueType,
            Cci.ITypeReference systemInt8Type,
            Cci.ITypeReference systemInt16Type,
            Cci.ITypeReference systemInt32Type,
            Cci.ITypeReference systemInt64Type,
            Cci.ICustomAttribute compilerGeneratedAttribute)
        {
            Debug.Assert(module != null);
            Debug.Assert(systemObject != null);
            Debug.Assert(systemValueType != null);

            _module = module;
            _systemObject = systemObject;
            _systemValueType = systemValueType;

            _systemInt8Type = systemInt8Type;
            _systemInt16Type = systemInt16Type;
            _systemInt32Type = systemInt32Type;
            _systemInt64Type = systemInt64Type;

            _compilerGeneratedAttribute = compilerGeneratedAttribute;
            _name = GetClassName(submissionSlotIndex);
        }

        internal static string GetClassName(int submissionSlotIndex)
        {
            return "<PrivateImplementationDetails>" + (submissionSlotIndex >= 0 ? submissionSlotIndex.ToString() : "");
        }

        internal void Freeze()
        {
            var wasFrozen = Interlocked.Exchange(ref _frozen, 1);
            if (wasFrozen != 0)
            {
                throw new InvalidOperationException();
            }

            // Sort data fields
            _orderedMappedFields = _mappedFields.Values.OrderBy((x, y) => x.Name.CompareTo(y.Name)).AsImmutable();
            _orderedSynthesizedMethods = _synthesizedMethods.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).AsImmutable();
            _orderedProxyTypes = _proxyTypes.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).AsImmutable();
        }

        private bool IsFrozen
        {
            get { return _frozen != 0; }
        }

        internal Cci.IFieldReference CreateDataField(ImmutableArray<byte> data)
        {
            Debug.Assert(!IsFrozen);
            Cci.ITypeReference type = _proxyTypes.GetOrAdd((uint)data.Length, size => GetStorageStruct(size));
            return _mappedFields.GetOrAdd(data, data0 =>
            {
                var name = GenerateDataFieldName(data0);
                var newField = new MappedField(name, this, type, data0);
                return newField;
            });
        }

        private Cci.ITypeReference GetStorageStruct(uint size)
        {
            switch (size)
            {
                case 1:
                    return _systemInt8Type ?? new ExplicitSizeStruct(1, this, _systemValueType);
                case 2:
                    return _systemInt16Type ?? new ExplicitSizeStruct(2, this, _systemValueType);
                case 4:
                    return _systemInt32Type ?? new ExplicitSizeStruct(4, this, _systemValueType);
                case 8:
                    return _systemInt64Type ?? new ExplicitSizeStruct(8, this, _systemValueType);
            }

            return new ExplicitSizeStruct(size, this, _systemValueType);
        }


        // Add a new synthesized method indexed by it's name if the method isn't already present.
        internal bool TryAddSynthesizedMethod(Cci.IMethodDefinition method)
        {
            Debug.Assert(!IsFrozen);
            return _synthesizedMethods.TryAdd(method.Name, method);
        }

        public override IEnumerable<Cci.IFieldDefinition> GetFields(EmitContext context)
        {
            Debug.Assert(IsFrozen);
            return _orderedMappedFields;
        }

        public override IEnumerable<Cci.IMethodDefinition> GetMethods(EmitContext context)
        {
            Debug.Assert(IsFrozen);
            return _orderedSynthesizedMethods;
        }

        // Get method by name, if one exists. Otherwise return null.
        internal Cci.IMethodDefinition GetMethod(string name)
        {
            Cci.IMethodDefinition method;
            _synthesizedMethods.TryGetValue(name, out method);
            return method;
        }

        public override IEnumerable<Cci.INestedTypeDefinition> GetNestedTypes(EmitContext context)
        {
            Debug.Assert(IsFrozen);
            return System.Linq.Enumerable.OfType<ExplicitSizeStruct>(_orderedProxyTypes);
        }

        public override string ToString()
        {
            return this.Name;
        }

        public override Cci.ITypeReference GetBaseClass(EmitContext context)
        {
            return _systemObject;
        }

        public override IEnumerable<Cci.ICustomAttribute> GetAttributes(EmitContext context)
        {
            if (_compilerGeneratedAttribute != null)
            {
                return SpecializedCollections.SingletonEnumerable(_compilerGeneratedAttribute);
            }

            return SpecializedCollections.EmptyEnumerable<Cci.ICustomAttribute>();
        }

        public override void Dispatch(Cci.MetadataVisitor visitor)
        {
            visitor.Visit((Cci.INamespaceTypeDefinition)this);
        }

        public override Cci.INamespaceTypeDefinition AsNamespaceTypeDefinition(EmitContext context)
        {
            return this;
        }

        public override Cci.INamespaceTypeReference AsNamespaceTypeReference
        {
            get { return this; }
        }

        public string Name
        {
            get { return _name; }
        }

        public bool IsPublic
        {
            get { return false; }
        }

        public Cci.IUnitReference GetUnit(EmitContext context)
        {
            Debug.Assert(context.Module == _module);
            return _module;
        }

        public string NamespaceName
        {
            get { return ""; }
        }

        internal static string GenerateDataFieldName(ImmutableArray<byte> data)
        {
            var hash = CryptographicHashProvider.ComputeSha1(data);
            char[] c = new char[hash.Length * 2];
            int i = 0;
            foreach (var b in hash)
            {
                c[i++] = Hexchar(b >> 4);
                c[i++] = Hexchar(b & 0xF);
            }

            return MemberNamePrefix + new string(c);
        }

        private static char Hexchar(int x)
        {
            return (char)((x <= 9) ? (x + '0') : (x + ('A' - 10)));
        }
    }

    /// <summary>
    /// Simple struct type with explicit size and no members.
    /// </summary>
    internal sealed class ExplicitSizeStruct : DefaultTypeDef, Cci.INestedTypeDefinition
    {
        private readonly uint _size;
        private readonly Cci.INamedTypeDefinition _containingType;
        private readonly Cci.ITypeReference _sysValueType;

        internal ExplicitSizeStruct(uint size, PrivateImplementationDetails containingType, Cci.ITypeReference sysValueType)
        {
            _size = size;
            _containingType = containingType;
            _sysValueType = sysValueType;
        }

        public override string ToString()
        {
            return _containingType.ToString() + "." + this.Name;
        }

        override public ushort Alignment
        {
            get { return 1; }
        }

        override public Cci.ITypeReference GetBaseClass(EmitContext context)
        {
            return _sysValueType;
        }

        override public LayoutKind Layout
        {
            get { return LayoutKind.Explicit; }
        }

        override public uint SizeOf
        {
            get { return _size; }
        }

        override public void Dispatch(Cci.MetadataVisitor visitor)
        {
            visitor.Visit((Cci.INestedTypeDefinition)this);
        }

        public string Name
        {
            get { return "__StaticArrayInitTypeSize=" + _size; }
        }

        public Cci.ITypeDefinition ContainingTypeDefinition
        {
            get { return _containingType; }
        }

        public Cci.TypeMemberVisibility Visibility
        {
            get { return Cci.TypeMemberVisibility.Private; }
        }

        public override bool IsValueType
        {
            get { return true; }
        }

        public Cci.ITypeReference GetContainingType(EmitContext context)
        {
            return _containingType;
        }

        public override Cci.INestedTypeDefinition AsNestedTypeDefinition(EmitContext context)
        {
            return this;
        }

        public override Cci.INestedTypeReference AsNestedTypeReference
        {
            get { return this; }
        }
    }

    /// <summary>
    /// Definition of a simple field mapped to a metadata block
    /// </summary>
    internal sealed class MappedField : Cci.IFieldDefinition
    {
        private readonly Cci.INamedTypeDefinition _containingType;
        private readonly Cci.ITypeReference _type;
        private readonly ImmutableArray<byte> _block;
        private readonly string _name;

        internal MappedField(string name, Cci.INamedTypeDefinition containingType, Cci.ITypeReference type, ImmutableArray<byte> block)
        {
            Debug.Assert(name != null);
            Debug.Assert(containingType != null);
            Debug.Assert(type != null);
            Debug.Assert(!block.IsDefault);

            _containingType = containingType;
            _type = type;
            _block = block;
            _name = name;
        }

        public override string ToString()
        {
            return string.Format("{0} {1}.{2}", _type, _containingType, this.Name);
        }

        public Cci.IMetadataConstant GetCompileTimeValue(EmitContext context)
        {
            return null;
        }

        public ImmutableArray<byte> MappedData
        {
            get { return _block; }
        }

        public bool IsCompileTimeConstant
        {
            get { return false; }
        }

        public bool IsNotSerialized
        {
            get { return false; }
        }

        public bool IsReadOnly
        {
            get { return true; }
        }

        public bool IsRuntimeSpecial
        {
            get { return false; }
        }

        public bool IsSpecialName
        {
            get { return false; }
        }

        public bool IsStatic
        {
            get { return true; }
        }

        public bool IsMarshalledExplicitly
        {
            get { return false; }
        }

        public Cci.IMarshallingInformation MarshallingInformation
        {
            get { return null; }
        }

        public ImmutableArray<byte> MarshallingDescriptor
        {
            get { return default(ImmutableArray<byte>); }
        }

        public uint Offset
        {
            get { throw ExceptionUtilities.Unreachable; }
        }

        public Cci.ITypeDefinition ContainingTypeDefinition
        {
            get { return _containingType; }
        }

        public Cci.TypeMemberVisibility Visibility
        {
            get { return Cci.TypeMemberVisibility.Assembly; }
        }

        public Cci.ITypeReference GetContainingType(EmitContext context)
        {
            return _containingType;
        }

        public IEnumerable<Cci.ICustomAttribute> GetAttributes(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.ICustomAttribute>();
        }

        public void Dispatch(Cci.MetadataVisitor visitor)
        {
            visitor.Visit((Cci.IFieldDefinition)this);
        }

        public Cci.IDefinition AsDefinition(EmitContext context)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public string Name
        {
            get { return _name; }
        }

        public bool IsContextualNamedEntity
        {
            get { return false; }
        }

        public Cci.ITypeReference GetType(EmitContext context)
        {
            return _type;
        }

        public Cci.IFieldDefinition GetResolvedField(EmitContext context)
        {
            return this;
        }

        public Cci.ISpecializedFieldReference AsSpecializedFieldReference
        {
            get { return null; }
        }

        public Cci.IMetadataConstant Constant
        {
            get { throw ExceptionUtilities.Unreachable; }
        }
    }

    /// <summary>
    /// Just a default implementation of a type definition.
    /// </summary>
    internal abstract class DefaultTypeDef : Cci.ITypeDefinition
    {
        public IEnumerable<Cci.IEventDefinition> Events
        {
            get { return SpecializedCollections.EmptyEnumerable<Cci.IEventDefinition>(); }
        }

        public IEnumerable<Cci.MethodImplementation> GetExplicitImplementationOverrides(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.MethodImplementation>();
        }

        virtual public IEnumerable<Cci.IFieldDefinition> GetFields(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.IFieldDefinition>();
        }

        public IEnumerable<Cci.IGenericTypeParameter> GenericParameters
        {
            get { return SpecializedCollections.EmptyEnumerable<Cci.IGenericTypeParameter>(); }
        }

        public ushort GenericParameterCount
        {
            get { return 0; }
        }

        public bool HasDeclarativeSecurity
        {
            get { return false; }
        }

        public IEnumerable<Cci.ITypeReference> Interfaces(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.ITypeReference>();
        }

        public bool IsAbstract
        {
            get { return false; }
        }

        public bool IsBeforeFieldInit
        {
            get { return false; }
        }

        public bool IsComObject
        {
            get { return false; }
        }

        public bool IsGeneric
        {
            get { return false; }
        }

        public bool IsInterface
        {
            get { return false; }
        }

        public bool IsRuntimeSpecial
        {
            get { return false; }
        }

        public bool IsSerializable
        {
            get { return false; }
        }

        public bool IsSpecialName
        {
            get { return false; }
        }

        public bool IsWindowsRuntimeImport
        {
            get { return false; }
        }

        public bool IsSealed
        {
            get { return true; }
        }

        public virtual IEnumerable<Cci.IMethodDefinition> GetMethods(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.IMethodDefinition>();
        }

        public virtual IEnumerable<Cci.INestedTypeDefinition> GetNestedTypes(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.INestedTypeDefinition>();
        }

        public IEnumerable<Cci.IPropertyDefinition> GetProperties(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.IPropertyDefinition>();
        }

        public IEnumerable<Cci.SecurityAttribute> SecurityAttributes
        {
            get { return SpecializedCollections.EmptyEnumerable<Cci.SecurityAttribute>(); }
        }

        public CharSet StringFormat
        {
            get { return CharSet.Ansi; }
        }

        public virtual IEnumerable<Cci.ICustomAttribute> GetAttributes(EmitContext context)
        {
            return SpecializedCollections.EmptyEnumerable<Cci.ICustomAttribute>();
        }

        public Cci.IDefinition AsDefinition(EmitContext context)
        {
            return this;
        }

        public bool IsEnum
        {
            get { return false; }
        }

        public Cci.ITypeDefinition GetResolvedType(EmitContext context)
        {
            return this;
        }

        public Cci.PrimitiveTypeCode TypeCode(EmitContext context)
        {
            return Cci.PrimitiveTypeCode.NotPrimitive;
        }

        public TypeDefinitionHandle TypeDef
        {
            get { throw ExceptionUtilities.Unreachable; }
        }

        public Cci.IGenericMethodParameterReference AsGenericMethodParameterReference
        {
            get { return null; }
        }

        public Cci.IGenericTypeInstanceReference AsGenericTypeInstanceReference
        {
            get { return null; }
        }

        public Cci.IGenericTypeParameterReference AsGenericTypeParameterReference
        {
            get { return null; }
        }

        public virtual Cci.INamespaceTypeDefinition AsNamespaceTypeDefinition(EmitContext context)
        {
            return null;
        }

        public virtual Cci.INamespaceTypeReference AsNamespaceTypeReference
        {
            get { return null; }
        }

        public Cci.ISpecializedNestedTypeReference AsSpecializedNestedTypeReference
        {
            get { return null; }
        }

        public virtual Cci.INestedTypeDefinition AsNestedTypeDefinition(EmitContext context)
        {
            return null;
        }

        public virtual Cci.INestedTypeReference AsNestedTypeReference
        {
            get { return null; }
        }

        public Cci.ITypeDefinition AsTypeDefinition(EmitContext context)
        {
            return this;
        }

        public bool MangleName
        {
            get { return false; }
        }

        public virtual ushort Alignment
        {
            get { return 0; }
        }

        public virtual Cci.ITypeReference GetBaseClass(EmitContext context)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public virtual LayoutKind Layout
        {
            get { return LayoutKind.Auto; }
        }

        public virtual uint SizeOf
        {
            get { return 0; }
        }

        public virtual void Dispatch(Cci.MetadataVisitor visitor)
        {
            throw ExceptionUtilities.Unreachable;
        }

        public virtual bool IsValueType
        {
            get { return false; }
        }
    }
}
