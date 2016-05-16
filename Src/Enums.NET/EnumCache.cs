﻿#region License
// Copyright (c) 2016 Tyler Brinkley
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using EnumsNET.Collections;
using EnumsNET.Numerics;

#if !(NET20 || NET35)
using System.Collections.Concurrent;
#endif

namespace EnumsNET
{
    internal sealed class EnumCache<TInt, TIntProvider>
        where TInt : struct, IFormattable, IConvertible, IComparable<TInt>, IEquatable<TInt>
        where TIntProvider : struct, INumericProvider<TInt>
    {
        #region Static
        internal static TIntProvider Provider = new TIntProvider();

        private static bool IsPowerOfTwo(TInt x) => Provider.And(x, Provider.Subtract(x, Provider.One)).Equals(Provider.Zero);

        private static bool IsNumeric(string value)
        {
            char firstChar;
            return value.Length > 0 && (char.IsDigit((firstChar = value[0])) || firstChar == '-' || firstChar == '+');
        }
        #endregion

        #region Fields
        internal readonly TInt AllFlags;

        internal readonly bool IsFlagEnum;

        internal readonly bool IsContiguous;

        private readonly string _enumTypeName;

        private readonly TInt _maxDefined;

        private readonly TInt _minDefined;

        private readonly Func<EnumFormat, Func<InternalEnumMember<TInt, TIntProvider>, string>> _getCustomFormatter;

        // The main collection of values, names, and attributes with ~O(1) retrieval on name or value
        // If constant contains a DescriptionAttribute it will be the first in the attribute array
        private readonly OrderedBiDirectionalDictionary<TInt, NameAndAttributes> _valueMap;

        // Duplicate values are stored here with a key of the constant's name, is null if no duplicates
        private readonly Dictionary<string, ValueAndAttributes<TInt>> _duplicateValues;

        private Dictionary<string, string> _ignoreCaseSet;

#if NET20 || NET35
        private Dictionary<EnumFormat, EnumParser> _customEnumFormatParsers;
#else
        private ConcurrentDictionary<EnumFormat, EnumParser> _customEnumFormatParsers;
#endif
        #endregion

        #region Properties
        // Enables case insensitive parsing, lazily instantiated to reduce memory usage if not going to use this feature, is thread-safe as it's only used for retrieval
        private Dictionary<string, string> IgnoreCaseSet
        {
            get
            {
                if (_ignoreCaseSet == null)
                {
                    var ignoreCaseSet = new Dictionary<string, string>(GetDefinedCount(false), StringComparer.OrdinalIgnoreCase);
                    foreach (var nameAndAttributes in _valueMap.SecondItems)
                    {
                        ignoreCaseSet[nameAndAttributes.Name] = nameAndAttributes.Name;
                    }
                    if (_duplicateValues != null)
                    {
                        foreach (var name in _duplicateValues.Keys)
                        {
                            ignoreCaseSet[name] = name;
                        }
                    }
                    _ignoreCaseSet = ignoreCaseSet;
                }
                return _ignoreCaseSet;
            }
        }
        #endregion

        public EnumCache(Type enumType, Func<EnumFormat, Func<InternalEnumMember<TInt, TIntProvider>, string>> getCustomFormatter)
        {
            Debug.Assert(enumType != null);
            Debug.Assert(enumType.IsEnum);
            _enumTypeName = enumType.Name;
            Debug.Assert(getCustomFormatter != null);
            _getCustomFormatter = getCustomFormatter;
            IsFlagEnum = enumType.IsDefined(typeof(FlagsAttribute), false);

            var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
            _valueMap = new OrderedBiDirectionalDictionary<TInt, NameAndAttributes>(fields.Length);
            if (fields.Length == 0)
            {
                return;
            }
            var duplicateValues = new Dictionary<string, ValueAndAttributes<TInt>>();
            foreach (var field in fields)
            {
                var value = (TInt)field.GetValue(null);
                var name = field.Name;
                var attributes = Attribute.GetCustomAttributes(field, false);
                var isMainDupe = false;
                if (attributes.Length > 0)
                {
                    var descriptionFound = false;
                    for (var i = 0; i < attributes.Length; ++i)
                    {
                        var attr = attributes[i];
                        if (!descriptionFound)
                        {
                            var descAttr = attr as DescriptionAttribute;
                            if (descAttr != null)
                            {
                                for (var j = i; j > 0; --j)
                                {
                                    attributes[j] = attributes[j - 1];
                                }
                                attributes[0] = descAttr;
                                if (descAttr.GetType() == typeof(DescriptionAttribute))
                                {
                                    descriptionFound = true;
                                    if (isMainDupe)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                        if (!isMainDupe && (attr as MainDuplicateAttribute) != null)
                        {
                            isMainDupe = true;
                            if (descriptionFound)
                            {
                                break;
                            }
                        }
                    }
                }
                var index = _valueMap.IndexOfFirst(value);
                if (index < 0)
                {
                    for (index = _valueMap.Count; index > 0; --index)
                    {
                        var mapValue = _valueMap.GetFirstAt(index - 1);
                        if (!Provider.LessThan(value, mapValue))
                        {
                            break;
                        }
                    }
                    _valueMap.Insert(index, value, new NameAndAttributes(name, attributes));
                    if (IsPowerOfTwo(value))
                    {
                        AllFlags = Provider.Or(AllFlags, value);
                    }
                }
                else
                {
                    if (isMainDupe)
                    {
                        var nameAndAttributes = _valueMap.GetSecondAt(index);
                        _valueMap.ReplaceSecondAt(index, new NameAndAttributes(name, attributes));
                        name = nameAndAttributes.Name;
                        attributes = nameAndAttributes.Attributes;
                    }
                    duplicateValues.Add(name, new ValueAndAttributes<TInt>(value, attributes));
                }
            }
            _maxDefined = _valueMap.GetFirstAt(_valueMap.Count - 1);
            _minDefined = _valueMap.GetFirstAt(0);
            IsContiguous = Provider.Subtract(_maxDefined, Provider.Create(_valueMap.Count - 1)).Equals(_minDefined);

            _valueMap.TrimExcess();
            if (duplicateValues.Count > 0)
            {
                // Makes sure is in increasing order, due to no removals
#if NET20
                var dupes = duplicateValues.ToArray();
                Array.Sort(dupes, (first, second) => InternalCompare(first.Value.Value, second.Value.Value));
#else
                var dupes = duplicateValues.OrderBy(pair => pair.Value.Value).ToList();
#endif
                _duplicateValues = new Dictionary<string, ValueAndAttributes<TInt>>(duplicateValues.Count);
                foreach (var pair in dupes)
                {
                    _duplicateValues.Add(pair.Key, pair.Value);
                }
            }
        }

        #region Standard Enum Operations
        #region Type Methods
        public int GetDefinedCount(bool uniqueValued) => _valueMap.Count + (uniqueValued ? 0 : _duplicateValues?.Count ?? 0);

        public IEnumerable<InternalEnumMember<TInt, TIntProvider>> GetEnumMembers(bool uniqueValued)
        {
            if (uniqueValued || _duplicateValues == null)
            {
                return _valueMap.Select(pair => new InternalEnumMember<TInt, TIntProvider>(pair.First, pair.Second.Name, pair.Second.Attributes, this));
            }
            else
            {
                return GetAllEnumMembersInValueOrder();
            }
        }

        public IEnumerable<string> GetNames(bool uniqueValued) => GetEnumMembers(uniqueValued).Select(member => member.Name);

        public IEnumerable<TInt> GetValues(bool uniqueValued) => GetEnumMembers(uniqueValued).Select(member => member.Value);

        private IEnumerable<InternalEnumMember<TInt, TIntProvider>> GetAllEnumMembersInValueOrder()
        {
            using (var mainEnumerator = _valueMap.GetEnumerator())
            {
                var mainIsActive = true;
                mainEnumerator.MoveNext();
                var mainPair = mainEnumerator.Current;
                using (IEnumerator<KeyValuePair<string, ValueAndAttributes<TInt>>> dupeEnumerator = _duplicateValues.GetEnumerator())
                {
                    var dupeIsActive = true;
                    dupeEnumerator.MoveNext();
                    var dupePair = dupeEnumerator.Current;
                    var count = GetDefinedCount(false);
                    for (var i = 0; i < count; ++i)
                    {
                        TInt value;
                        string name;
                        Attribute[] attributes;
                        if (dupeIsActive && (!mainIsActive || Provider.LessThan(dupePair.Value.Value, mainPair.First)))
                        {
                            value = dupePair.Value.Value;
                            name = dupePair.Key;
                            attributes = dupePair.Value.Attributes;
                            if (dupeIsActive = dupeEnumerator.MoveNext())
                            {
                                dupePair = dupeEnumerator.Current;
                            }
                        }
                        else
                        {
                            value = mainPair.First;
                            name = mainPair.Second.Name;
                            attributes = mainPair.Second.Attributes;
                            if (mainIsActive = mainEnumerator.MoveNext())
                            {
                                mainPair = mainEnumerator.Current;
                            }
                        }
                        yield return new InternalEnumMember<TInt, TIntProvider>(value, name, attributes, this);
                    }
                }
            }
        }
        #endregion

        #region IsValid
        public bool IsValid(object value)
        {
            Preconditions.NotNull(value, nameof(value));

            TInt result;
            return TryToObject(value, out result, true);
        }

        public bool IsValid(TInt value) => IsFlagEnum ? IsValidFlagCombination(value) : IsDefined(value);

        public bool IsValid(long value) => Provider.IsInValueRange(value) && IsValid(Provider.Create(value));

        public bool IsValid(ulong value) => Provider.IsInValueRange(value) && IsValid(Provider.Create(value));
        #endregion

        #region IsDefined
        public bool IsDefined(object value)
        {
            Preconditions.NotNull(value, nameof(value));

            TInt result;
            return TryToObject(value, out result, false) && IsDefined(result);
        }

        public bool IsDefined(TInt value) => IsContiguous ? (!(Provider.LessThan(value, _minDefined) || Provider.LessThan(_maxDefined, value))) : _valueMap.ContainsFirst(value);

        public bool IsDefined(string name, bool ignoreCase)
        {
            Preconditions.NotNull(name, nameof(name));

            return _valueMap.ContainsSecond(new NameAndAttributes(name)) || (_duplicateValues?.ContainsKey(name) ?? false) || (ignoreCase && IgnoreCaseSet.ContainsKey(name));
        }

        public bool IsDefined(long value) => Provider.IsInValueRange(value) && IsDefined(Provider.Create(value));

        public bool IsDefined(ulong value) => Provider.IsInValueRange(value) && IsDefined(Provider.Create(value));
        #endregion

        #region ToObject
        public TInt ToObject(object value, bool validate)
        {
            Preconditions.NotNull(value, nameof(value));

            if (value is TInt || value is TInt?)
            {
                var result = (TInt)value;
                if (validate)
                {
                    Validate(result, nameof(value));
                }
                return result;
            }

            var type = value.GetType();

            switch (Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type))
            {
                case TypeCode.SByte:
                    return ToObject((sbyte)value, validate);
                case TypeCode.Byte:
                    return ToObject((byte)value, validate);
                case TypeCode.Int16:
                    return ToObject((short)value, validate);
                case TypeCode.UInt16:
                    return ToObject((ushort)value, validate);
                case TypeCode.Int32:
                    return ToObject((int)value, validate);
                case TypeCode.UInt32:
                    return ToObject((uint)value, validate);
                case TypeCode.Int64:
                    return ToObject((long)value, validate);
                case TypeCode.UInt64:
                    return ToObject((ulong)value, validate);
                case TypeCode.String:
                    var result = Parse((string)value, false, null);
                    if (validate)
                    {
                        Validate(result, nameof(value));
                    }
                    return result;
            }
            throw new ArgumentException($"value is not type {_enumTypeName}, SByte, Int16, Int32, Int64, Byte, UInt16, UInt32, UInt64, or String.");
        }

        public TInt ToObject(long value, bool validate)
        {
            if (!Provider.IsInValueRange(value))
            {
                throw new OverflowException("value is outside the underlying type's value range");
            }

            var result = Provider.Create(value);
            if (validate)
            {
                Validate(result, nameof(value));
            }
            return result;
        }

        public TInt ToObject(ulong value, bool validate)
        {
            if (!Provider.IsInValueRange(value))
            {
                throw new OverflowException("value is outside the underlying type's value range");
            }

            var result = Provider.Create(value);
            if (validate)
            {
                Validate(result, nameof(value));
            }
            return result;
        }

        public bool TryToObject(object value, out TInt result, bool validate)
        {
            if (value != null)
            {
                if (value is TInt || value is TInt?)
                {
                    result = (TInt)value;
                    return true;
                }

                var type = value.GetType();

                switch (Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type))
                {
                    case TypeCode.SByte:
                        return TryToObject((sbyte)value, out result, validate);
                    case TypeCode.Byte:
                        return TryToObject((byte)value, out result, validate);
                    case TypeCode.Int16:
                        return TryToObject((short)value, out result, validate);
                    case TypeCode.UInt16:
                        return TryToObject((ushort)value, out result, validate);
                    case TypeCode.Int32:
                        return TryToObject((int)value, out result, validate);
                    case TypeCode.UInt32:
                        return TryToObject((uint)value, out result, validate);
                    case TypeCode.Int64:
                        return TryToObject((long)value, out result, validate);
                    case TypeCode.UInt64:
                        return TryToObject((ulong)value, out result, validate);
                    case TypeCode.String:
                        if (TryParse((string)value, false, out result, null))
                        {
                            return !validate || IsValid(result);
                        }
                        break;
                }
            }
            result = Provider.Zero;
            return false;
        }

        public bool TryToObject(long value, out TInt result, bool validate)
        {
            if (Provider.IsInValueRange(value))
            {
                result = Provider.Create(value);
                return !validate || IsValid(result);
            }
            result = Provider.Zero;
            return false;
        }

        public bool TryToObject(ulong value, out TInt result, bool validate)
        {
            if (Provider.IsInValueRange(value))
            {
                result = Provider.Create(value);
                return !validate || IsValid(result);
            }
            result = Provider.Zero;
            return false;
        }
        #endregion

        #region All Values Main Methods
        public void Validate(TInt value, string paramName)
        {
            if (!IsValid(value))
            {
                throw new ArgumentException($"invalid value of {AsString(value)} for {_enumTypeName}", paramName);
            }
        }

        public string AsString(TInt value) => InternalAsString(GetEnumMember(value));

        internal string InternalAsString(InternalEnumMember<TInt, TIntProvider> member)
        {
            if (IsFlagEnum)
            {
                var str = InternalFormatAsFlags(member, null, null);
                if (str != null)
                {
                    return str;
                }
            }
            return InternalFormat(member, EnumFormat.Name, EnumFormat.DecimalValue);
        }

        public string AsString(TInt value, EnumFormat[] formats) => InternalAsString(GetEnumMember(value), formats);

        internal string InternalAsString(InternalEnumMember<TInt, TIntProvider> member, EnumFormat[] formats) => formats?.Length > 0 ? InternalFormat(member, formats) : InternalAsString(member);

        public string AsString(TInt value, string format) => InternalAsString(GetEnumMember(value), format);

        internal string InternalAsString(InternalEnumMember<TInt, TIntProvider> member, string format) => string.IsNullOrEmpty(format) ? InternalAsString(member) : InternalFormat(member, format);

        public string Format(TInt value, EnumFormat format) => InternalFormat(GetEnumMember(value), format);

        public string Format(TInt value, EnumFormat format0, EnumFormat format1) => InternalFormat(GetEnumMember(value), format0, format1);

        public string Format(TInt value, EnumFormat format0, EnumFormat format1, EnumFormat format2) => InternalFormat(GetEnumMember(value), format0, format1, format2);

        public string Format(TInt value, EnumFormat[] formats)
        {
            Preconditions.NotNull(formats, nameof(formats));

            return InternalFormat(GetEnumMember(value), formats);
        }

        public string Format(TInt value, string format)
        {
            Preconditions.NotNull(format, nameof(format));

            return InternalFormat(GetEnumMember(value), format);
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, string format)
        {
            switch (format)
            {
                case "G":
                case "g":
                    return InternalAsString(member);
                case "F":
                case "f":
                    return InternalFormatAsFlags(member, null, null) ?? InternalFormat(member, EnumFormat.Name, EnumFormat.DecimalValue);
                case "D":
                case "d":
                    return member.Value.ToString("D", null);
                case "X":
                case "x":
                    return member.Value.ToString(Provider.HexFormatString, null);
            }
            throw new FormatException("format string can be only \"G\", \"g\", \"X\", \"x\", \"F\", \"f\", \"D\" or \"d\".");
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat format)
        {
            switch (format)
            {
                case EnumFormat.DecimalValue:
                    return member.Value.ToString("D", null);
                case EnumFormat.HexadecimalValue:
                    return member.Value.ToString(Provider.HexFormatString, null);
                case EnumFormat.Name:
                    return member.Name;
                case EnumFormat.Description:
                    return member.Description;
                default:
                    return _getCustomFormatter(format)?.Invoke(member);
            }
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat format0, EnumFormat format1)
        {
            return InternalFormat(member, format0) ?? InternalFormat(member, format1);
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat format0, EnumFormat format1, EnumFormat format2)
        {
            return InternalFormat(member, format0) ?? InternalFormat(member, format1) ?? InternalFormat(member, format2);
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat format0, EnumFormat format1, EnumFormat format2, EnumFormat format3)
        {
            return InternalFormat(member, format0) ?? InternalFormat(member, format1) ?? InternalFormat(member, format2) ?? InternalFormat(member, format3);
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat format0, EnumFormat format1, EnumFormat format2, EnumFormat format3, EnumFormat format4)
        {
            return InternalFormat(member, format0) ?? InternalFormat(member, format1) ?? InternalFormat(member, format2) ?? InternalFormat(member, format3) ?? InternalFormat(member, format4);
        }

        internal string InternalFormat(InternalEnumMember<TInt, TIntProvider> member, EnumFormat[] formats)
        {
            foreach (var format in formats)
            {
                var formattedValue = InternalFormat(member, format);
                if (formattedValue != null)
                {
                    return formattedValue;
                }
            }
            return null;
        }
        #endregion

        #region Defined Values Main Methods
        public InternalEnumMember<TInt, TIntProvider> GetEnumMember(TInt value)
        {
            var index = _valueMap.IndexOfFirst(value);
            if (index >= 0)
            {
                var nameAndAttributes = _valueMap.GetSecondAt(index);
                return new InternalEnumMember<TInt, TIntProvider>(value, nameAndAttributes.Name, nameAndAttributes.Attributes, this);
            }
            return new InternalEnumMember<TInt, TIntProvider>(value, null, null, this);
        }

        public InternalEnumMember<TInt, TIntProvider> GetEnumMember(string name, bool ignoreCase)
        {
            Preconditions.NotNull(name, nameof(name));

            return InternalGetEnumMember(name, ignoreCase);
        }

        private InternalEnumMember<TInt, TIntProvider> InternalGetEnumMember(string name, bool ignoreCase)
        {
            var index = _valueMap.IndexOfSecond(new NameAndAttributes(name));
            if (index < 0)
            {
                var valueAndAttributes = default(ValueAndAttributes<TInt>);
                bool foundInDuplicates;
                if (!(foundInDuplicates = (_duplicateValues?.TryGetValue(name, out valueAndAttributes)).GetValueOrDefault()))
                {
                    if (!(ignoreCase && IgnoreCaseSet.TryGetValue(name, out name)))
                    {
                        return new InternalEnumMember<TInt, TIntProvider>();
                    }
                    index = _valueMap.IndexOfSecond(new NameAndAttributes(name));
                    if (index < 0)
                    {
                        valueAndAttributes = _duplicateValues[name];
                        foundInDuplicates = true;
                    }
                }
                if (foundInDuplicates)
                {
                    return new InternalEnumMember<TInt, TIntProvider>(valueAndAttributes.Value, name, valueAndAttributes.Attributes, this);
                }
            }
            var pair = _valueMap.GetAt(index);
            return new InternalEnumMember<TInt, TIntProvider>(pair.First, name, pair.Second.Attributes, this);
        }

        public string GetName(TInt value) => GetEnumMember(value).Name;

        public string GetDescription(TInt value) => GetEnumMember(value).Description;
        #endregion

        #region Attributes
        public bool HasAttribute<TAttribute>(TInt value)
            where TAttribute : Attribute => GetEnumMember(value).HasAttribute<TAttribute>();

        public TAttribute GetAttribute<TAttribute>(TInt value)
            where TAttribute : Attribute => GetEnumMember(value).GetAttribute<TAttribute>();

        public TResult GetAttributeSelect<TAttribute, TResult>(TInt value, Func<TAttribute, TResult> selector, TResult defaultValue)
            where TAttribute : Attribute => GetEnumMember(value).GetAttributeSelect(selector, defaultValue);

        public bool TryGetAttributeSelect<TAttribute, TResult>(TInt value, Func<TAttribute, TResult> selector, out TResult result)
            where TAttribute : Attribute => GetEnumMember(value).TryGetAttributeSelect(selector, out result);

        public IEnumerable<TAttribute> GetAttributes<TAttribute>(TInt value)
            where TAttribute : Attribute => GetEnumMember(value).GetAttributes<TAttribute>();

        public IEnumerable<Attribute> GetAttributes(TInt value) => GetEnumMember(value).Attributes;
        #endregion

        #region Parsing
        public TInt Parse(string value, bool ignoreCase, EnumFormat[] parseFormatOrder)
        {
            Preconditions.NotNull(value, nameof(value));

            value = value.Trim();
            TInt result;
            if (IsFlagEnum)
            {
                return Provider.TryParse(value, NumberStyles.AllowLeadingSign, null, out result) ? result : ParseFlags(value, ignoreCase, null, parseFormatOrder);
            }

            if (!(parseFormatOrder?.Length > 0))
            {
                parseFormatOrder = Enums.DefaultFormatOrder;
            }

            if (InternalTryParse(value, ignoreCase, out result, parseFormatOrder))
            {
                return result;
            }
            if (IsNumeric(value))
            {
                throw new OverflowException("value is outside the underlying type's value range");
            }
            throw new ArgumentException($"string was not recognized as being a member of {_enumTypeName}", nameof(value));
        }

        public bool TryParse(string value, bool ignoreCase, out TInt result, EnumFormat[] parseFormatOrder)
        {
            if (value != null)
            {
                value = value.Trim();
                if (IsFlagEnum)
                {
                    return Provider.TryParse(value, NumberStyles.AllowLeadingSign, null, out result) || TryParseFlags(value, ignoreCase, null, out result, parseFormatOrder);
                }

                if (!(parseFormatOrder?.Length > 0))
                {
                    parseFormatOrder = Enums.DefaultFormatOrder;
                }

                if (InternalTryParse(value, ignoreCase, out result, parseFormatOrder))
                {
                    return true;
                }
            }
            result = Provider.Zero;
            return false;
        }

        private bool InternalTryParse(string value, bool ignoreCase, out TInt result, EnumFormat[] parseFormatOrder)
        {
            result = default(TInt);
            foreach (var format in parseFormatOrder)
            {
                var success = false;
                switch (format)
                {
                    case EnumFormat.DecimalValue:
                        success = Provider.TryParse(value, NumberStyles.AllowLeadingSign, null, out result);
                        break;
                    case EnumFormat.HexadecimalValue:
                        success = Provider.TryParse(value, NumberStyles.AllowHexSpecifier, null, out result);
                        break;
                    case EnumFormat.Name:
                        var member = InternalGetEnumMember(value, ignoreCase);
                        if (member.IsDefined)
                        {
                            result = member.Value;
                            success = true;
                        }
                        break;
                    default:
                        EnumParser parser = null;
#if NET20
                        lock (_valueMap)
                        {
#endif
                            if (_customEnumFormatParsers?.TryGetValue(format, out parser) != true)
                            {
                                if (format == EnumFormat.Description)
                                {
                                    parser = new EnumParser(internalMember => internalMember.Description, this);
                                }
                                else
                                {
                                    var customEnumFormatter = _getCustomFormatter(format);
                                    if (customEnumFormatter != null)
                                    {
                                        parser = new EnumParser(customEnumFormatter, this);
                                    }
                                }
                                if (parser != null)
                                {
                                    if (_customEnumFormatParsers == null)
                                    {
                                        Interlocked.CompareExchange(ref _customEnumFormatParsers,
#if NET20
                                            new Dictionary<EnumFormat, EnumParser>(new EnumComparer<EnumFormat>()),
#else
                                            new ConcurrentDictionary<EnumFormat, EnumParser>(new EnumComparer<EnumFormat>()),
#endif
                                            null);
                                    }
#if NET20
                                    _customEnumFormatParsers.Add(format, parser);
#else
                                    _customEnumFormatParsers.TryAdd(format, parser);
#endif
                                }
                            }
#if NET20
                        }
#endif
                        success = parser?.TryParse(value, ignoreCase, out result) ?? false;
                        break;
                }
                if (success)
                {
                    return true;
                }
            }
            return false;
        }
        #endregion
        #endregion

        #region Flag Enum Operations
        #region Main Methods
        public bool IsValidFlagCombination(TInt value) => Provider.And(AllFlags, value).Equals(value);

        public string FormatAsFlags(TInt value, string delimiter, EnumFormat[] formats) => InternalFormatAsFlags(GetEnumMember(value), delimiter, formats);

        private string InternalFormatAsFlags(InternalEnumMember<TInt, TIntProvider> member, string delimiter, EnumFormat[] formats)
        {
            if (!IsValidFlagCombination(member.Value))
            {
                return null;
            }

            if (string.IsNullOrEmpty(delimiter))
            {
                delimiter = FlagEnums.DefaultDelimiter;
            }

            if (!(formats?.Length > 0))
            {
                formats = Enums.DefaultFormatOrder;
            }
            
            if (member.IsDefined || member.Value.Equals(Provider.Zero))
            {
                return InternalFormat(member, formats);
            }

            return string.Join(delimiter,
                InternalGetFlags(member.Value).Select(flag => InternalFormat(GetEnumMember(flag), formats))
#if NET20 || NET35
                .ToArray()
#endif
                );
        }

        public IEnumerable<TInt> GetFlags(TInt value) => IsValidFlagCombination(value) ? InternalGetFlags(value) : null;

        private IEnumerable<TInt> InternalGetFlags(TInt value)
        {
            var isLessThanZero = Provider.LessThan(value, Provider.Zero);
            for (var currentValue = Provider.One; (isLessThanZero || !Provider.LessThan(value, currentValue)) && !currentValue.Equals(Provider.Zero); currentValue = Provider.LeftShift(currentValue, 1))
            {
                if (IsValidFlagCombination(currentValue) && InternalHasAnyFlags(value, currentValue))
                {
                    yield return currentValue;
                }
            }
        }

        public bool HasAnyFlags(TInt value)
        {
            ValidateFlagCombination(value, nameof(value));
            return !value.Equals(Provider.Zero);
        }

        public bool HasAnyFlags(TInt value, TInt flagMask)
        {
            ValidateFlagCombination(value, nameof(value));
            ValidateFlagCombination(flagMask, nameof(flagMask));
            return InternalHasAnyFlags(value, flagMask);
        }

        private bool InternalHasAnyFlags(TInt value, TInt flagMask) => !Provider.And(value, flagMask).Equals(Provider.Zero);

        public bool HasAllFlags(TInt value)
        {
            ValidateFlagCombination(value, nameof(value));
            return value.Equals(AllFlags);
        }

        public bool HasAllFlags(TInt value, TInt flagMask)
        {
            ValidateFlagCombination(value, nameof(value));
            ValidateFlagCombination(flagMask, nameof(flagMask));
            return Provider.And(value, flagMask).Equals(flagMask);
        }

        public TInt ToggleFlags(TInt value)
        {
            ValidateFlagCombination(value, nameof(value));
            return Provider.Xor(value, AllFlags);
        }

        public TInt ToggleFlags(TInt value, TInt flagMask)
        {
            ValidateFlagCombination(value, nameof(value));
            ValidateFlagCombination(flagMask, nameof(flagMask));
            return Provider.Xor(value, flagMask);
        }

        public TInt CommonFlags(TInt value, TInt flagMask)
        {
            ValidateFlagCombination(value, nameof(value));
            ValidateFlagCombination(flagMask, nameof(flagMask));
            return Provider.And(value, flagMask);
        }

        public TInt SetFlags(TInt flag0, TInt flag1)
        {
            ValidateFlagCombination(flag0, nameof(flag0));
            ValidateFlagCombination(flag1, nameof(flag1));
            return Provider.Or(flag0, flag1);
        }

        public TInt SetFlags(TInt flag0, TInt flag1, TInt flag2)
        {
            ValidateFlagCombination(flag0, nameof(flag0));
            ValidateFlagCombination(flag1, nameof(flag1));
            ValidateFlagCombination(flag2, nameof(flag2));
            return Provider.Or(Provider.Or(flag0, flag1), flag2);
        }

        public TInt SetFlags(TInt flag0, TInt flag1, TInt flag2, TInt flag3)
        {
            ValidateFlagCombination(flag0, nameof(flag0));
            ValidateFlagCombination(flag1, nameof(flag1));
            ValidateFlagCombination(flag2, nameof(flag2));
            ValidateFlagCombination(flag3, nameof(flag3));
            return Provider.Or(Provider.Or(Provider.Or(flag0, flag1), flag2), flag3);
        }

        public TInt SetFlags(TInt flag0, TInt flag1, TInt flag2, TInt flag3, TInt flag4)
        {
            ValidateFlagCombination(flag0, nameof(flag0));
            ValidateFlagCombination(flag1, nameof(flag1));
            ValidateFlagCombination(flag2, nameof(flag2));
            ValidateFlagCombination(flag3, nameof(flag3));
            ValidateFlagCombination(flag4, nameof(flag4));
            return Provider.Or(Provider.Or(Provider.Or(Provider.Or(flag0, flag1), flag2), flag3), flag4);
        }

        public TInt SetFlags(IEnumerable<TInt> flags)
        {
            var combinedFlags = Provider.Zero;
            if (flags != null)
            {
                foreach (var flag in flags)
                {
                    ValidateFlagCombination(flag, nameof(flags) + " must contain all valid flag combinations");
                    combinedFlags = Provider.Or(combinedFlags, flag);
                }
            }
            return combinedFlags;
        }

        public TInt ClearFlags(TInt value, TInt flagMask)
        {
            ValidateFlagCombination(value, nameof(value));
            ValidateFlagCombination(flagMask, nameof(flagMask));
            return Provider.And(value, Provider.Xor(flagMask, AllFlags));
        }

        private void ValidateFlagCombination(TInt value, string paramName)
        {
            if (!IsValidFlagCombination(value))
            {
                throw new ArgumentException("must be valid flag combination", paramName);
            }
        }
        #endregion

        #region Parsing
        public TInt ParseFlags(string value, bool ignoreCase, string delimiter, EnumFormat[] parseFormatOrder)
        {
            Preconditions.NotNull(value, nameof(value));

            if (string.IsNullOrEmpty(delimiter))
            {
                delimiter = FlagEnums.DefaultDelimiter;
            }

            var effectiveDelimiter = delimiter.Trim();
            if (effectiveDelimiter.Length == 0)
            {
                effectiveDelimiter = delimiter;
            }

            if (!(parseFormatOrder?.Length > 0))
            {
                parseFormatOrder = Enums.DefaultFormatOrder;
            }

            var result = Provider.Zero;
            var startIndex = 0;
            var valueLength = value.Length;
            while (startIndex < valueLength)
            {
                while (startIndex < valueLength && char.IsWhiteSpace(value[startIndex]))
                {
                    ++startIndex;
                }
                var delimiterIndex = value.IndexOf(effectiveDelimiter, startIndex, StringComparison.Ordinal);
                if (delimiterIndex < 0)
                {
                    delimiterIndex = valueLength;
                }
                var newStartIndex = delimiterIndex + effectiveDelimiter.Length;
                while (delimiterIndex > startIndex && char.IsWhiteSpace(value[delimiterIndex - 1]))
                {
                    --delimiterIndex;
                }
                var indValue = value.Substring(startIndex, delimiterIndex - startIndex);
                TInt indValueAsInt;
                if (InternalTryParse(indValue, ignoreCase, out indValueAsInt, parseFormatOrder))
                {
                    if (!IsValidFlagCombination(indValueAsInt))
                    {
                        throw new ArgumentException("All individual enum values within value must be valid");
                    }
                    result = Provider.Or(result, indValueAsInt);
                }
                else
                {
                    if (IsNumeric(indValue))
                    {
                        throw new OverflowException("value is outside the underlying type's value range");
                    }
                    throw new ArgumentException("value is not a valid combination of flag enum values");
                }
                startIndex = newStartIndex;
            }
            return result;
        }

        public bool TryParseFlags(string value, bool ignoreCase, string delimiter, out TInt result, EnumFormat[] parseFormatOrder)
        {
            if (value == null)
            {
                result = Provider.Zero;
                return false;
            }

            if (string.IsNullOrEmpty(delimiter))
            {
                delimiter = FlagEnums.DefaultDelimiter;
            }

            var effectiveDelimiter = delimiter.Trim();
            if (effectiveDelimiter.Length == 0)
            {
                effectiveDelimiter = delimiter;
            }

            if (!(parseFormatOrder?.Length > 0))
            {
                parseFormatOrder = Enums.DefaultFormatOrder;
            }

            var resultAsInt = Provider.Zero;
            var startIndex = 0;
            var valueLength = value.Length;
            while (startIndex < valueLength)
            {
                while (startIndex < valueLength && char.IsWhiteSpace(value[startIndex]))
                {
                    ++startIndex;
                }
                var delimiterIndex = value.IndexOf(effectiveDelimiter, startIndex, StringComparison.Ordinal);
                if (delimiterIndex < 0)
                {
                    delimiterIndex = valueLength;
                }
                var newStartIndex = delimiterIndex + effectiveDelimiter.Length;
                while (delimiterIndex > startIndex && char.IsWhiteSpace(value[delimiterIndex - 1]))
                {
                    --delimiterIndex;
                }
                var indValue = value.Substring(startIndex, delimiterIndex - startIndex);
                TInt indValueAsInt;
                if (!InternalTryParse(indValue, ignoreCase, out indValueAsInt, parseFormatOrder) || !IsValidFlagCombination(indValueAsInt))
                {
                    result = Provider.Zero;
                    return false;
                }
                resultAsInt = Provider.Or(resultAsInt, indValueAsInt);
                startIndex = newStartIndex;
            }
            result = resultAsInt;
            return true;
        }
        #endregion
        #endregion

        #region Nested Types
        internal sealed class EnumParser
        {
            private readonly Dictionary<string, TInt> _formatValueMap;
            private Dictionary<string, TInt> _formatIgnoreCase;

            private Dictionary<string, TInt> FormatIgnoreCase
            {
                get
                {
                    if (_formatIgnoreCase == null)
                    {
                        var formatIgnoreCase = new Dictionary<string, TInt>(_formatValueMap.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in _formatValueMap)
                        {
                            formatIgnoreCase[pair.Key] = pair.Value;
                        }
                        
                        _formatIgnoreCase = formatIgnoreCase;
                    }
                    return _formatIgnoreCase;
                }
            }

            public EnumParser(Func<InternalEnumMember<TInt, TIntProvider>, string> formatter, EnumCache<TInt, TIntProvider> enumCache)
            {
                _formatValueMap = new Dictionary<string, TInt>(enumCache.GetDefinedCount(false));
                foreach (var member in enumCache.GetEnumMembers(false))
                {
                    var formattedValue = formatter(member);
                    if (formattedValue != null)
                    {
                        _formatValueMap[formattedValue] = member.Value;
                    }
                }
            }

            internal bool TryParse(string formattedValue, bool ignoreCase, out TInt result) => _formatValueMap.TryGetValue(formattedValue, out result) || (ignoreCase && FormatIgnoreCase.TryGetValue(formattedValue, out result));
        }
        #endregion
    }
}