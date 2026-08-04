// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Runtime.Serialization;
using Microsoft.Coyote.Runtime;
using SystemGenerics = System.Collections.Generic;

namespace Microsoft.Coyote.Rewriting.Types.Collections.Generic
{
#pragma warning disable CA1000 // Do not declare static members on generic types
    /// <summary>
    /// Provides methods for creating generic dictionaries that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Dictionary<TKey, TValue>
    {
        /// <summary>
        /// Initializes a new dictionary instance class that is empty, has the default initial
        /// capacity, and uses the default equality comparer for the key type.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create() =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper() :
            new SystemGenerics.Dictionary<TKey, TValue>();

        /// <summary>
        /// Initializes a new dictionary instance class that contains elements copied from the
        /// specified dictionary and uses the default equality comparer for the key type.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            SystemGenerics.IDictionary<TKey, TValue> dictionary) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(dictionary) :
            new SystemGenerics.Dictionary<TKey, TValue>(dictionary);

        /// <summary>
        /// Initializes a new dictionary instance class that is empty, has the default
        /// initial capacity, and uses the specified equality comparer.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            SystemGenerics.IEqualityComparer<TKey> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(comparer) :
            new SystemGenerics.Dictionary<TKey, TValue>(comparer);

        /// <summary>
        /// Initializes a new dictionary instance class that is empty, has the specified initial
        /// capacity, and uses the default equality comparer for the key type.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(int capacity) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(capacity) :
            new SystemGenerics.Dictionary<TKey, TValue>(capacity);

        /// <summary>
        /// Initializes a new dictionary instance class that contains elements copied from the specified dictionary
        /// and uses the specified equality comparer.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            SystemGenerics.IDictionary<TKey, TValue> dictionary,
            SystemGenerics.IEqualityComparer<TKey> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(dictionary, comparer) :
            new SystemGenerics.Dictionary<TKey, TValue>(dictionary, comparer);

        /// <summary>
        /// Initializes a new dictionary instance class that is empty, has the specified initial
        /// capacity, and uses the specified equality comparer.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            int capacity, SystemGenerics.IEqualityComparer<TKey> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(capacity, comparer) :
            new SystemGenerics.Dictionary<TKey, TValue>(capacity, comparer);

#if NET
        /// <summary>
        /// Initializes a new dictionary instance class that contains elements copied
        /// from the specified enumerable.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            SystemGenerics.IEnumerable<SystemGenerics.KeyValuePair<TKey, TValue>> collection) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(collection) :
            new SystemGenerics.Dictionary<TKey, TValue>(collection);

        /// <summary>
        /// Initializes a new dictionary instance class that contains elements copied
        /// from the specified enumerable and uses the specified equality comparer.
        /// </summary>
        public static SystemGenerics.Dictionary<TKey, TValue> Create(
            SystemGenerics.IEnumerable<SystemGenerics.KeyValuePair<TKey, TValue>> collection,
            SystemGenerics.IEqualityComparer<TKey> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(collection, comparer) :
            new SystemGenerics.Dictionary<TKey, TValue>(collection, comparer);
#endif

        /// <summary>
        /// Opens the data-race guard for one access to the specified dictionary, or returns a
        /// no-op scope when the dictionary is not a modelled instance.
        /// </summary>
        /// <remarks>
        /// A helper rather than an inline conditional access, because a scope is a struct and so
        /// cannot be produced by the null-conditional operator. The scope must be opened and closed
        /// inside the shim: rewriting replaces call sites, never the surrounding user method, so
        /// there is nowhere else the guard could span the operation from.
        /// </remarks>
        /// <param name="instance">The dictionary being accessed.</param>
        /// <param name="isWriteAccess">True if the access can modify the dictionary.</param>
        /// <returns>The scope guarding this access.</returns>
        private static DataRaceChecker.Scope Enter(
            SystemGenerics.Dictionary<TKey, TValue> instance, bool isWriteAccess) =>
            instance is Wrapper wrapper ? wrapper.Checker.Enter(isWriteAccess) : default;

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static TValue get_Item(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance[key];
        }

        /// <summary>
        /// Sets the value associated with the specified key.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static void set_Item(SystemGenerics.Dictionary<TKey, TValue> instance,
            TKey key, TValue value)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, true);
            instance[key] = value;
        }

        /// <summary>
        /// Gets a collection containing the keys in the dictionary.
        /// </summary>
        /// <remarks>
        /// The returned collection is the real one, and enumerating it is NOT guarded: only the
        /// call that hands it out is. Guarding the enumeration would require modelling the
        /// enumerator types themselves.
        /// </remarks>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemGenerics.Dictionary<TKey, TValue>.KeyCollection get_Keys(
            SystemGenerics.Dictionary<TKey, TValue> instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance.Keys;
        }

        /// <summary>
        /// Gets a collection containing the values in the dictionary.
        /// </summary>
        /// <remarks>Enumerating the result is not guarded; see <see cref="get_Keys"/>.</remarks>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemGenerics.Dictionary<TKey, TValue>.ValueCollection get_Values(
            SystemGenerics.Dictionary<TKey, TValue> instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance.Values;
        }

        /// <summary>
        /// Gets the number of key/value pairs contained in the dictionary.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static int get_Count(SystemGenerics.Dictionary<TKey, TValue> instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance.Count;
        }

        /// <summary>
        /// Adds the specified key and value to the dictionary.
        /// </summary>
        public static void Add(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key, TValue value)
        {
            using var scope = Enter(instance, true);
            instance.Add(key, value);
        }

        /// <summary>
        /// Removes all keys and values from the dictionary.
        /// </summary>
        public static void Clear(SystemGenerics.Dictionary<TKey, TValue> instance)
        {
            using var scope = Enter(instance, true);
            instance.Clear();
        }

        /// <summary>
        /// Determines whether the dictionary contains the specified key.
        /// </summary>
        public static bool ContainsKey(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key)
        {
            using var scope = Enter(instance, false);
            return instance.ContainsKey(key);
        }

        /// <summary>
        /// Determines whether the dictionary contains a specific value.
        /// </summary>
        public static bool ContainsValue(SystemGenerics.Dictionary<TKey, TValue> instance, TValue value)
        {
            using var scope = Enter(instance, false);
            return instance.ContainsValue(value);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the dictionary.
        /// </summary>
        /// <remarks>Enumerating the result is not guarded; see <see cref="get_Keys"/>.</remarks>
        public static SystemGenerics.Dictionary<TKey, TValue>.Enumerator GetEnumerator(
            SystemGenerics.Dictionary<TKey, TValue> instance)
        {
            using var scope = Enter(instance, false);
            return instance.GetEnumerator();
        }

        /// <summary>
        /// Removes the value with the specified key from the dictionary,
        /// and copies the element to the value parameter.
        /// </summary>
        public static bool Remove(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key)
        {
            using var scope = Enter(instance, true);
            return instance.Remove(key);
        }

        /// <summary>
        /// Gets the value associated with the specified key.
        /// </summary>
        public static bool TryGetValue(SystemGenerics.Dictionary<TKey, TValue> instance,
            TKey key, out TValue value)
        {
            using var scope = Enter(instance, false);
            return instance.TryGetValue(key, out value);
        }

        /// <summary>
        /// Implements the <see cref="ISerializable"/> interface and returns the data needed
        /// to serialize the dictionary instance.
        /// </summary>
        #if NET8_0_OR_GREATER
        [Obsolete("Marking obsolete", DiagnosticId = "SYSLIB0051")]
        #endif
        public static void GetObjectData(SystemGenerics.Dictionary<TKey, TValue> instance,
            SerializationInfo info, StreamingContext context)
        {
            using var scope = Enter(instance, true);
            instance.GetObjectData(info, context);
        }

        /// <summary>
        /// Implements the <see cref="ISerializable"/> interface and raises
        /// the deserialization event when the deserialization is complete.
        /// </summary>
        public static void OnDeserialization(SystemGenerics.Dictionary<TKey, TValue> instance, object sender)
        {
            using var scope = Enter(instance, true);
            instance.OnDeserialization(sender);
        }

#if NET
        /// <summary>
        /// Ensures that the dictionary can hold up to a specified number of entries without
        /// any further expansion of its backing storage.
        /// </summary>
        public static int EnsureCapacity(SystemGenerics.Dictionary<TKey, TValue> instance, int capacity)
        {
            using var scope = Enter(instance, true);
            return instance.EnsureCapacity(capacity);
        }

        /// <summary>
        /// Removes the value with the specified key from the dictionary.
        /// </summary>
        public static bool Remove(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key, out TValue value)
        {
            using var scope = Enter(instance, true);
            return instance.Remove(key, out value);
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally
        /// initialized with all its entries.
        /// </summary>
        public static void TrimExcess(SystemGenerics.Dictionary<TKey, TValue> instance)
        {
            using var scope = Enter(instance, true);
            instance.TrimExcess();
        }

        /// <summary>
        /// Sets the capacity of this dictionary to hold up a specified number of entries
        /// without any further expansion of its backing storage.
        /// </summary>
        public static void TrimExcess(SystemGenerics.Dictionary<TKey, TValue> instance, int capacity)
        {
            using var scope = Enter(instance, true);
            instance.TrimExcess(capacity);
        }

        /// <summary>
        /// Attempts to add the specified key and value to the dictionary.
        /// </summary>
        public static bool TryAdd(SystemGenerics.Dictionary<TKey, TValue> instance, TKey key, TValue value)
        {
            using var scope = Enter(instance, true);
            return instance.TryAdd(key, value);
        }
#endif

        /// <summary>
        /// Wraps a dictionary so that it can be controlled during testing.
        /// </summary>
        private class Wrapper : SystemGenerics.Dictionary<TKey, TValue>
        {
            /// <summary>
            /// Detects unsynchronized concurrent access to this dictionary.
            /// </summary>
            internal readonly DataRaceChecker Checker =
                new DataRaceChecker(typeof(SystemGenerics.Dictionary<TKey, TValue>));

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper()
                : base()
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IDictionary<TKey, TValue> dictionary)
                : base(dictionary)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(int capacity)
                : base(capacity)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(int capacity, SystemGenerics.IEqualityComparer<TKey> comparer)
                : base(capacity, comparer)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IDictionary<TKey, TValue> dictionary,
                SystemGenerics.IEqualityComparer<TKey> comparer)
                : base(dictionary, comparer)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IEqualityComparer<TKey> comparer)
                : base(comparer)
            {
            }

#if NET
            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IEnumerable<SystemGenerics.KeyValuePair<TKey, TValue>> collection)
                : base(collection)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IEnumerable<SystemGenerics.KeyValuePair<TKey, TValue>> collection,
                SystemGenerics.IEqualityComparer<TKey> comparer)
                : base(collection, comparer)
            {
            }
#endif
        }
    }
#pragma warning restore CA1000 // Do not declare static members on generic types
}
