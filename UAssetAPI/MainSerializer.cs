using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

#if DEBUGVERBOSE
using System.Diagnostics;
#endif

namespace UAssetAPI
{
    /// <summary>
    /// An entry in the property type registry. Contains the class Type used for standard and struct property serialization.
    /// </summary>
    internal class RegistryEntry
    {
        internal Type PropertyType;
        internal bool HasCustomStructSerialization;
        internal Func<FName, PropertyData> Creator;

        public RegistryEntry()
        {

        }
    }

    /// <summary>
    /// The main serializer for most property types in UAssetAPI.
    /// </summary>
    public static class MainSerializer
    {
#if DEBUGVERBOSE
        private static PropertyData lastType;
#endif
        public static string[] AdditionalPropertyRegistry = ["ClassProperty", "SoftClassProperty", "AssetClassProperty"];

        private static IDictionary<string, RegistryEntry> _propertyTypeRegistry;

        /// <summary>
        /// The property type registry. Maps serialized property names to their types.
        /// </summary>
        internal static IDictionary<string, RegistryEntry> PropertyTypeRegistry
        {
            get => _propertyTypeRegistry;
            set => _propertyTypeRegistry = value; // I hope you know what you're doing!
        }

        static MainSerializer()
        {
            InitializePropertyTypeRegistry();
        }

        private static IEnumerable<Assembly> GetDependentAssemblies(Assembly analyzedAssembly)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Where(a => GetNamesOfAssembliesReferencedBy(a).Contains(analyzedAssembly.FullName));
        }

        public static IEnumerable<string> GetNamesOfAssembliesReferencedBy(Assembly assembly)
        {
            return assembly.GetReferencedAssemblies().Select(assemblyName => assemblyName.FullName);
        }

        private static Type registryParentDataType = typeof(PropertyData);

        /// <summary>
        /// Initializes the property type registry.
        /// </summary>
        private static void InitializePropertyTypeRegistry()
        {
            if (_propertyTypeRegistry != null) return;
            _propertyTypeRegistry = new Dictionary<string, RegistryEntry>();

            Assembly[] allDependentAssemblies = GetDependentAssemblies(registryParentDataType.Assembly).ToArray();
            Assembly[] allAssemblies = new Assembly[allDependentAssemblies.Length + 1];
            allAssemblies[0] = registryParentDataType.Assembly;
            Array.Copy(allDependentAssemblies, 0, allAssemblies, 1, allDependentAssemblies.Length);

            for (int i = 0; i < allAssemblies.Length; i++)
            {
                Type[] allPropertyDataTypes = allAssemblies[i].GetTypes().Where(t => t.IsSubclassOf(registryParentDataType)).ToArray();
                for (int j = 0; j < allPropertyDataTypes.Length; j++)
                {
                    Type currentPropertyDataType = allPropertyDataTypes[j];
                    if (currentPropertyDataType == null || currentPropertyDataType.ContainsGenericParameters) continue;

                    var testInstance = Activator.CreateInstance(currentPropertyDataType);

                    FString returnedPropType = currentPropertyDataType.GetProperty("PropertyType")?.GetValue(testInstance, null) as FString;
                    if (returnedPropType == null) continue;
                    bool? returnedHasCustomStructSerialization = currentPropertyDataType.GetProperty("HasCustomStructSerialization")?.GetValue(testInstance, null) as bool?;
                    if (returnedHasCustomStructSerialization == null) continue;
                    bool? returnedShouldBeRegistered = currentPropertyDataType.GetProperty("ShouldBeRegistered")?.GetValue(testInstance, null) as bool?;
                    if (returnedShouldBeRegistered == null) continue;

                    if ((bool)returnedShouldBeRegistered)
                    {
                        RegistryEntry res = new RegistryEntry();
                        res.PropertyType = currentPropertyDataType;
                        res.HasCustomStructSerialization = (bool)returnedHasCustomStructSerialization;

                        var nameParam = Expression.Parameter(typeof(FName));
                        res.Creator = Expression.Lambda<Func<FName, PropertyData>>(
                           Expression.New(currentPropertyDataType.GetConstructor(new[] { typeof(FName), }), new[] { nameParam, }),
                           nameParam
                        ).Compile();

                        _propertyTypeRegistry[returnedPropType.Value] = res;
                    }
                }
            }

            // Fetch the current git commit while we're here
            UAPUtils.CurrentCommit = string.Empty;
            using (Stream stream = registryParentDataType.Assembly.GetManifestResourceStream("UAssetAPI.git_commit.txt"))
            {
                if (stream != null)
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        if (reader != null) UAPUtils.CurrentCommit = reader.ReadToEnd().Trim();
                    }
                }
            }
        }

        /// <summary>
        /// Generates an unversioned header based on a list of properties, and sorts the list in the correct order to be serialized.
        /// This implementation mirrors UE 5.5.4's FUnversionedHeaderBuilder pattern:
        /// - Walks all schema properties in order, calling IncludeProperty/ExcludeProperty
        /// - Finalize() trims trailing skip-only fragments
        /// - TrimZeroMask removes zero-mask bits for fragments without any zeroes
        /// </summary>
        /// <param name="data">The list of properties to sort and generate an unversioned header from.</param>
        /// <param name="parentName">The name of the parent of all the properties.</param>
        /// <param name="parentModulePath">The path to the module that the parent class/struct of this property is contained within.</param>
        /// <param name="asset">The UAsset which the properties are contained within.</param>
        public static FUnversionedHeader GenerateUnversionedHeader(ref List<PropertyData> data, FName parentName, FName parentModulePath, UAsset asset)
        {
            if (!asset.HasUnversionedProperties) return null;
            if (asset.Mappings == null) return null;

            // Build a map from schema index -> PropertyData for all properties we want to serialize
            Dictionary<int, PropertyData> propMap = new Dictionary<int, PropertyData>();
            HashSet<int> zeroProps = new HashSet<int>();
            foreach (PropertyData entry in data)
            {
                if (!asset.Mappings.TryGetProperty<UsmapProperty>(entry.Name, entry.Ancestry, entry.ArrayIndex, asset, out _, out int idx))
                    throw new FormatException("No valid property \"" + entry.Name.ToString() + "\" in class " + entry.Ancestry.Parent.ToString());
                propMap[idx] = entry;
                if (entry.CanBeZero(asset) && entry.IsZero) zeroProps.Add(idx);
            }

            // Get total number of schema properties to walk
            IList<UsmapProperty> allSchemaProps = asset.Mappings.GetAllProperties(parentName?.ToString(), parentModulePath?.ToString(), asset);
            int totalSchemaProps = allSchemaProps.Count;

            // === FUnversionedHeaderBuilder pattern (mirrors UE 5.5.4) ===
            // Walk all schema indices 0..totalSchemaProps-1, building fragments incrementally
            List<FFragment> fragments = new List<FFragment>();
            fragments.Add(new FFragment()); // start with one defaulted fragment
            List<bool> zeroMaskBits = new List<bool>();
            bool bHasNonZeroValues = false;
            var sortedProps = new List<PropertyData>();

            for (int schemaIdx = 0; schemaIdx < totalSchemaProps; schemaIdx++)
            {
                if (propMap.ContainsKey(schemaIdx))
                {
                    // IncludeProperty
                    bool isZero = zeroProps.Contains(schemaIdx);

                    if (fragments[fragments.Count - 1].ValueNum == FFragment.ValueMax)
                    {
                        // TrimZeroMask: remove zero-mask bits for fragments without any zeroes
                        TrimZeroMask(fragments[fragments.Count - 1], zeroMaskBits);
                        fragments.Add(new FFragment());
                    }

                    fragments[fragments.Count - 1].ValueNum++;
                    fragments[fragments.Count - 1].bHasAnyZeroes |= isZero;
                    zeroMaskBits.Add(isZero);
                    bHasNonZeroValues |= !isZero;

                    sortedProps.Add(propMap[schemaIdx]);
                }
                else
                {
                    // ExcludeProperty
                    if (fragments[fragments.Count - 1].ValueNum > 0 || fragments[fragments.Count - 1].SkipNum == FFragment.SkipMax)
                    {
                        TrimZeroMask(fragments[fragments.Count - 1], zeroMaskBits);
                        fragments.Add(new FFragment());
                    }

                    fragments[fragments.Count - 1].SkipNum++;
                }
            }

            // Finalize: TrimZeroMask on last fragment, trim trailing skips, mark last
            TrimZeroMask(fragments[fragments.Count - 1], zeroMaskBits);

            // Trim trailing skip-only fragments
            while (fragments.Count > 1 && fragments[fragments.Count - 1].ValueNum == 0)
            {
                fragments.RemoveAt(fragments.Count - 1);
            }

            fragments[fragments.Count - 1].bIsLast = true;

            // Assign FirstNum for each fragment (needed for read-side iteration)
            int firstNum = 0;
            for (int i = 0; i < fragments.Count; i++)
            {
                fragments[i].FirstNum = firstNum + fragments[i].SkipNum;
                firstNum = fragments[i].FirstNum + fragments[i].ValueNum;
            }

#if DEBUGVERBOSE
            foreach (var frag in fragments) Debug.WriteLine("W: " + frag);
#endif

            BitArray zeroMask = new BitArray(zeroMaskBits.ToArray());

            var res = new FUnversionedHeader();
            res.Fragments = new LinkedList<FFragment>();
            foreach (var frag in fragments) res.Fragments.AddLast(frag);
            res.ZeroMask = zeroMask;
            res.bHasNonZeroValues = bHasNonZeroValues;
            if (res.Fragments.Count > 0)
            {
                res.CurrentFragment = res.Fragments.First;
                res.UnversionedPropertyIndex = res.CurrentFragment.Value.FirstNum;
            }

            data.Clear();
            data.AddRange(sortedProps);
            return res;
        }

        /// <summary>
        /// Mirrors UE's FUnversionedHeaderBuilder::TrimZeroMask.
        /// If figure has no zeroes, remove its zero-mask bits from the end of the list.
        /// </summary>
        private static void TrimZeroMask(FFragment fragment, List<bool> zeroMaskBits)
        {
            if (!fragment.bHasAnyZeroes && fragment.ValueNum > 0)
            {
                int removeCount = Math.Min(fragment.ValueNum, zeroMaskBits.Count);
                zeroMaskBits.RemoveRange(zeroMaskBits.Count - removeCount, removeCount);
            }
        }

        /// <summary>
        /// Initializes the correct PropertyData class based off of serialized name, type, etc.
        /// </summary>
        /// <param name="type">The serialized type of this property.</param>
        /// <param name="name">The serialized name of this property.</param>
        /// <param name="ancestry">The ancestry of the parent of this property.</param>
        /// <param name="parentName">The name of the parent class/struct of this property.</param>
        /// <param name="parentModulePath">The path to the module that the parent class/struct of this property is contained within.</param>
        /// <param name="asset">The UAsset which this property is contained within.</param>
        /// <param name="reader">The BinaryReader to read from. If left unspecified, you must call the <see cref="PropertyData.Read(AssetBinaryReader, bool, long, long, PropertySerializationContext)"/> method manually.</param>
        /// <param name="leng">The length of this property on disk in bytes.</param>
        /// <param name="propertyTagFlags">Property tag flags, if available.</param>
        /// <param name="ArrayIndex">The duplication index of this property.</param>
        /// <param name="includeHeader">Does this property serialize its header in the current context?</param>
        /// <param name="isZero">Is the body of this property empty?</param>
        /// <param name="propertyTypeName">The complete property type name, if available.</param>
        /// <returns>A new PropertyData instance based off of the passed parameters.</returns>
        public static PropertyData TypeToClass(FName type, FName name, AncestryInfo ancestry, FName parentName, FName parentModulePath, UAsset asset, AssetBinaryReader reader = null, int leng = 0, EPropertyTagFlags propertyTagFlags = EPropertyTagFlags.None, int ArrayIndex = 0, bool includeHeader = true, bool isZero = false, FPropertyTypeName propertyTypeName = null)
        {
            long startingOffset = 0;
            if (reader != null) startingOffset = reader.BaseStream.Position;

            if (type.Value.Value == "None") return null;

            PropertyData data = null;
            if (PropertyTypeRegistry.ContainsKey(type.Value.Value))
            {
                data = PropertyTypeRegistry[type.Value.Value].Creator.Invoke(name);
            }
            else
            {
#if DEBUGVERBOSE
                Debug.WriteLine("-----------");
                Debug.WriteLine("Parsing unknown type " + type.ToString());
                Debug.WriteLine("Length: " + leng);
                if (reader != null) Debug.WriteLine("Pos: " + reader.BaseStream.Position);
                Debug.WriteLine("Last type: " + lastType.PropertyType?.ToString());
                if (lastType is ArrayPropertyData) Debug.WriteLine("Last array's type was " + ((ArrayPropertyData)lastType).ArrayType?.ToString());
                if (lastType is StructPropertyData) Debug.WriteLine("Last struct's type was " + ((StructPropertyData)lastType).StructType?.ToString());
                if (lastType is MapPropertyData lastTypeMap)
                {
                    if (lastTypeMap.Value.Count == 0)
                    {
                        Debug.WriteLine("Last map's key type was " + lastTypeMap.KeyType?.ToString());
                        Debug.WriteLine("Last map's value type was " + lastTypeMap.ValueType?.ToString());
                    }
                    else
                    {
                        Debug.WriteLine("Last map's key type was " + lastTypeMap.Value.Keys.ElementAt(0).PropertyType?.ToString());
                        Debug.WriteLine("Last map's value type was " + lastTypeMap.Value[0].PropertyType?.ToString());
                    }
                }
                Debug.WriteLine("-----------");
#endif
                if (leng > 0)
                {
                    data = new UnknownPropertyData(name);
                    ((UnknownPropertyData)data).SetSerializingPropertyType(type.Value);
                }
                else
                {
                    if (reader == null) throw new FormatException("Unknown property type: " + type.ToString() + " (on " + name.ToString() + ")");
                    throw new FormatException("Unknown property type: " + type.ToString() + " (on " + name.ToString() + " at " + reader.BaseStream.Position + ")");
                }
            }

#if DEBUGVERBOSE
            lastType = data;
#endif

            data.IsZero = isZero;
            data.PropertyTagFlags = propertyTagFlags;
            data.Ancestry.Initialize(ancestry, parentName, parentModulePath);
            data.ArrayIndex = ArrayIndex;
            data.PropertyTypeName = propertyTypeName;
            if (reader != null && !isZero)
            {
                long posBefore = reader.BaseStream.Position;
                try
                {
                    data.Read(reader, includeHeader, leng);
                }
                catch (Exception)
                {
                    // if asset is unversioned, bubble the error up to make the whole export fail
                    // because unversioned headers aren't properly reconstructed currently
                    if (data is StructPropertyData && !reader.Asset.HasUnversionedProperties)
                    {
                        reader.BaseStream.Position = posBefore;
                        data = new RawStructPropertyData(name);
                        data.Ancestry.Initialize(ancestry, parentName, parentModulePath);
                        data.ArrayIndex = ArrayIndex;
                        data.PropertyTypeName = propertyTypeName;
                        data.Read(reader, includeHeader, leng);
                    }
                    else
                    {
                        throw;
                    }
                }
                if (data.Offset == 0) data.Offset = startingOffset; // fallback
            }
            else if (reader != null && isZero)
            {
                data.InitializeZero(reader);
            }

            return data;
        }

        /// <summary>
        /// Reads a property into memory.
        /// </summary>
        /// <param name="reader">The BinaryReader to read from. The underlying stream should be at the position of the property to be read.</param>
        /// <param name="ancestry">The ancestry of the parent of this property.</param>
        /// <param name="parentName">The name of the parent class/struct of this property.</param>
        /// <param name="parentModulePath">The path to the module that the parent class/struct of this property is contained within.</param>
        /// <param name="header">The unversioned header to be used when reading this property. Leave null if none exists.</param>
        /// <param name="includeHeader">Does this property serialize its header in the current context?</param>
        /// <returns>The property read from disk.</returns>
        public static PropertyData Read(AssetBinaryReader reader, AncestryInfo ancestry, FName parentName, FName parentModulePath, FUnversionedHeader header, bool includeHeader)
        {
            long startingOffset = reader.BaseStream.Position;
            FName name = null;
            FName type = null;
            int leng = 0;
            EPropertyTagFlags propertyTagFlags = EPropertyTagFlags.None;
            FPropertyTypeName typeName = null;
            int ArrayIndex = 0;
            string structType = null;
            bool isZero = false;

            if (reader.Asset.HasUnversionedProperties)
            {
                if (reader.Asset.Mappings == null)
                {
                    throw new InvalidMappingsException();
                }

                UsmapSchema relevantSchema = reader.Asset.Mappings.GetSchemaFromName(parentName?.ToString(), reader.Asset, parentModulePath?.ToString());
                while (header.UnversionedPropertyIndex > header.CurrentFragment.Value.LastNum)
                {
                    if (header.CurrentFragment.Value.bIsLast) return null;
                    header.CurrentFragment = header.CurrentFragment.Next;
                    header.UnversionedPropertyIndex = header.CurrentFragment.Value.FirstNum;
                }

                int practicingUnversionedPropertyIndex = header.UnversionedPropertyIndex;
                while (practicingUnversionedPropertyIndex >= relevantSchema.PropCount) // if needed, go to parent struct
                {
                    practicingUnversionedPropertyIndex -= relevantSchema.PropCount;

                    if (relevantSchema.SuperType != null && relevantSchema.SuperTypeModulePath != null && reader.Asset.Mappings.Schemas.ContainsKey(relevantSchema.SuperTypeModulePath + "." + relevantSchema.SuperType))
                    {
                        relevantSchema = reader.Asset.Mappings.Schemas[relevantSchema.SuperTypeModulePath + "." + relevantSchema.SuperType];
                    }
                    else if (relevantSchema.SuperType != null && reader.Asset.Mappings.Schemas.ContainsKey(relevantSchema.SuperType) && relevantSchema.Name != relevantSchema.SuperType) // name is insufficient if name of super is same as name of child
                    {
                        relevantSchema = reader.Asset.Mappings.Schemas[relevantSchema.SuperType];
                    }
                    else
                    {
                        relevantSchema = null;
                    }

                    if (relevantSchema == null) throw new FormatException("Failed to find a valid property for schema index " + header.UnversionedPropertyIndex + " in the class " + parentName.ToString());
                }
                UsmapProperty relevantProperty = relevantSchema.Properties[practicingUnversionedPropertyIndex];
                header.UnversionedPropertyIndex += 1;

                name = FName.DefineDummy(reader.Asset, relevantProperty.Name);
                type = FName.DefineDummy(reader.Asset, relevantProperty.PropertyData.Type.ToString());
                leng = 1; // not serialized
                ArrayIndex = relevantProperty.ArrayIndex;
                if (relevantProperty.PropertyData is UsmapStructData usmapStruc) structType = usmapStruc.StructType;

                // check if property is zero
                if (header.CurrentFragment.Value.bHasAnyZeroes)
                {
                    isZero = header.ZeroMaskIndex >= header.ZeroMask.Count ? false : header.ZeroMask.Get(header.ZeroMaskIndex);
                    header.ZeroMaskIndex++;
                }
            }
            else if (reader.Asset.ObjectVersionUE5 >= ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
            {
                name = reader.ReadFName();
                if (name.Value.Value == "None") return null;

                typeName = new FPropertyTypeName(reader);
                type = typeName.GetName();
                leng = reader.ReadInt32();
                propertyTagFlags = (EPropertyTagFlags)reader.ReadByte();

                if (propertyTagFlags.HasFlag(EPropertyTagFlags.HasArrayIndex))
                {
                    ArrayIndex = reader.ReadInt32();
                }
            }
            else
            {
                name = reader.ReadFName();
                if (name.Value.Value == "None") return null;

                type = reader.ReadFName();

                leng = reader.ReadInt32();
                ArrayIndex = reader.ReadInt32();
            }

            PropertyData result = TypeToClass(type, name, ancestry, parentName, parentModulePath, reader.Asset, reader, leng, propertyTagFlags, ArrayIndex, includeHeader, isZero, typeName);
            if (structType != null && result is StructPropertyData strucProp) strucProp.StructType = FName.DefineDummy(reader.Asset, structType);
            result.Offset = startingOffset;
            //Debug.WriteLine(type);
            return result;
        }

        internal static readonly Regex allNonLetters = new Regex("[^a-zA-Z]", RegexOptions.Compiled);

        /// <summary>
        /// Reads an FProperty into memory. Primarily used as a part of <see cref="StructExport"/> serialization.
        /// </summary>
        /// <param name="reader">The BinaryReader to read from. The underlying stream should be at the position of the FProperty to be read.</param>
        /// <returns>The FProperty read from disk.</returns>
        public static FProperty ReadFProperty(AssetBinaryReader reader)
        {
            FName serializedType = reader.ReadFName();
            Type requestedType = Type.GetType("UAssetAPI.FieldTypes.F" + allNonLetters.Replace(serializedType.Value.Value, string.Empty));
            if (requestedType == null) requestedType = typeof(FGenericProperty);
            var res = (FProperty)Activator.CreateInstance(requestedType);
            res.SerializedType = serializedType;
            res.Read(reader);
            return res;
        }

        /// <summary>
        /// Serializes an FProperty from memory.
        /// </summary>
        /// <param name="prop">The FProperty to serialize.</param>
        /// <param name="writer">The BinaryWriter to serialize the FProperty to.</param>
        public static void WriteFProperty(FProperty prop, AssetBinaryWriter writer)
        {
            writer.Write(prop.SerializedType);
            prop.Write(writer);
        }

        /// <summary>
        /// Reads a UProperty into memory. Primarily used as a part of <see cref="PropertyExport"/> serialization.
        /// </summary>
        /// <param name="reader">The BinaryReader to read from. The underlying stream should be at the position of the UProperty to be read.</param>
        /// <param name="serializedType">The type of UProperty to be read.</param>
        /// <returns>The FProperty read from disk.</returns>
        public static UProperty ReadUProperty(AssetBinaryReader reader, FName serializedType)
        {
            return ReadUProperty(reader, Type.GetType("UAssetAPI.FieldTypes.U" + allNonLetters.Replace(serializedType.Value.Value, string.Empty)));
        }

        /// <summary>
        /// Reads a UProperty into memory. Primarily used as a part of <see cref="PropertyExport"/> serialization.
        /// </summary>
        /// <param name="reader">The BinaryReader to read from. The underlying stream should be at the position of the UProperty to be read.</param>
        /// <param name="requestedType">The type of UProperty to be read.</param>
        /// <returns>The FProperty read from disk.</returns>
        public static UProperty ReadUProperty(AssetBinaryReader reader, Type requestedType)
        {
            if (requestedType == null) requestedType = typeof(UGenericProperty);
            var res = (UProperty)Activator.CreateInstance(requestedType);
            res.Read(reader);
            return res;
        }

        /// <summary>
        /// Reads a UProperty into memory. Primarily used as a part of <see cref="PropertyExport"/> serialization.
        /// </summary>
        /// <param name="reader">The BinaryReader to read from. The underlying stream should be at the position of the UProperty to be read.</param>
        /// <returns>The FProperty read from disk.</returns>
        public static T ReadUProperty<T>(AssetBinaryReader reader) where T : UProperty
        {
            var res = (UProperty)Activator.CreateInstance(typeof(T));
            res.Read(reader);
            return (T)res;
        }

        /// <summary>
        /// Serializes a UProperty from memory.
        /// </summary>
        /// <param name="prop">The UProperty to serialize.</param>
        /// <param name="writer">The BinaryWriter to serialize the UProperty to.</param>
        public static void WriteUProperty(UProperty prop, AssetBinaryWriter writer)
        {
            prop.Write(writer);
        }

        /// <summary>
        /// Serializes a property from memory.
        /// </summary>
        /// <param name="property">The property to serialize.</param>
        /// <param name="writer">The BinaryWriter to serialize the property to.</param>
        /// <param name="includeHeader">Does this property serialize its header in the current context?</param>
        /// <returns>The serial offset where the length of the property is stored.</returns>
        public static int Write(PropertyData property, AssetBinaryWriter writer, bool includeHeader)
        {
            if (property == null) return -1;

            property.Offset = writer.BaseStream.Position;

            if (writer.Asset.HasUnversionedProperties)
            {
                if (!property.IsZero || !property.CanBeZero(writer.Asset)) property.Write(writer, includeHeader);
                return -1; // length is not serialized
            }
            else if (writer.Asset.ObjectVersionUE5 >= ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
            {
                writer.Write(property.Name);
                if (property is UnknownPropertyData unknownProp)
                {
                    writer.Write(new FName(writer.Asset, unknownProp.SerializingPropertyType));
                    writer.Write((int)0);
                }
                else
                {
                    property.PropertyTypeName.Write(writer);
                }

                // update flags appropriately
                if (property is BoolPropertyData bProp)
                {
                    if (bProp.Value) property.PropertyTagFlags |= EPropertyTagFlags.BoolTrue;
                    else property.PropertyTagFlags &= ~EPropertyTagFlags.BoolTrue;
                }

                if (property.ArrayIndex != 0) property.PropertyTagFlags |= EPropertyTagFlags.HasArrayIndex;
                else property.PropertyTagFlags &= ~EPropertyTagFlags.HasArrayIndex;

                if (property.PropertyGuid != null) property.PropertyTagFlags |= EPropertyTagFlags.HasPropertyGuid;
                else property.PropertyTagFlags &= ~EPropertyTagFlags.HasPropertyGuid;

                int oldLoc = (int)writer.BaseStream.Position;
                writer.Write((int)0); // initial length
                writer.Write((byte)property.PropertyTagFlags);
                if (property.ArrayIndex != 0) writer.Write(property.ArrayIndex);
                int realLength = property.Write(writer, includeHeader);
                int newLoc = (int)writer.BaseStream.Position;

                writer.Seek(oldLoc, SeekOrigin.Begin);
                writer.Write(realLength);
                writer.Seek(newLoc, SeekOrigin.Begin);
                return oldLoc;
            }
            else
            {
                writer.Write(property.Name);
                if (property is UnknownPropertyData unknownProp)
                {
                    writer.Write(new FName(writer.Asset, unknownProp.SerializingPropertyType));
                }
                else if (property is RawStructPropertyData)
                {
                    writer.Write(new FName(writer.Asset, FString.FromString("StructProperty")));
                }
                else
                {
                    writer.Write(new FName(writer.Asset, property.PropertyType));
                }
                int oldLoc = (int)writer.BaseStream.Position;
                writer.Write((int)0); // initial length
                writer.Write(property.ArrayIndex);
                int realLength = property.Write(writer, includeHeader);
                int newLoc = (int)writer.BaseStream.Position;

                writer.Seek(oldLoc, SeekOrigin.Begin);
                writer.Write(realLength);
                writer.Seek(newLoc, SeekOrigin.Begin);
                return oldLoc;
            }

        }
    }
}
