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
    /// Provides methods for creating generic hashsets that can be controlled during testing.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly in code.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class HashSet<T>
    {
        /// <summary>
        /// Initializes a hash set instance class that is empty and uses the
        /// default equality comparer for the set type.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create() =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper() :
            new SystemGenerics.HashSet<T>();

        /// <summary>
        /// Initializes a hash set instance class that uses the default equality comparer
        /// for the set type, contains elements copied from the specified collection, and
        /// has sufficient capacity to accommodate the number of elements copied.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create(SystemGenerics.IEnumerable<T> collection) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(collection) :
            new SystemGenerics.HashSet<T>(collection);

        /// <summary>
        /// Initializes a hash set instance class that is empty and uses the default
        /// equality comparer for the set type.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create(SystemGenerics.IEqualityComparer<T> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(comparer) :
            new SystemGenerics.HashSet<T>(comparer);

        /// <summary>
        /// Initializes a hash set instance class that uses the specified equality comparer for the
        /// set type, contains elements copied from the specified collection, and has sufficient
        /// capacity to accommodate the number of elements copied.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create(SystemGenerics.IEnumerable<T> collection,
            SystemGenerics.IEqualityComparer<T> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(collection, comparer) :
            new SystemGenerics.HashSet<T>(collection, comparer);

#if NET
        /// <summary>
        /// Initializes a hash set instance class that is empty, but has reserved
        /// space for 'capacity' items and and uses the default equality comparer for the set type.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create(int capacity) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(capacity) :
            new SystemGenerics.HashSet<T>(capacity);

        /// <summary>
        /// Initializes a hash set instance class that uses the specified
        /// equality comparer for the set type, and has sufficient capacity to accommodate 'capacity' elements.
        /// </summary>
        public static SystemGenerics.HashSet<T> Create(int capacity,
            SystemGenerics.IEqualityComparer<T> comparer) =>
            CoyoteRuntime.IsExecutionControlled ?
            new Wrapper(capacity, comparer) :
            new SystemGenerics.HashSet<T>(capacity, comparer);
#endif

        /// <summary>
        /// Opens the data-race guard for one access to the specified hash set, or returns a no-op
        /// scope when the hash set is not a modelled instance.
        /// </summary>
        /// <remarks>
        /// A helper rather than an inline conditional access, because a scope is a struct and so
        /// cannot be produced by the null-conditional operator. The scope must be opened and closed
        /// inside the shim: rewriting replaces call sites, never the surrounding user method, so
        /// there is nowhere else the guard could span the operation from.
        /// </remarks>
        /// <param name="instance">The hash set being accessed.</param>
        /// <param name="isWriteAccess">True if the access can modify the hash set.</param>
        /// <returns>The scope guarding this access.</returns>
        private static DataRaceChecker.Scope Enter(SystemGenerics.HashSet<T> instance, bool isWriteAccess) =>
            instance is Wrapper wrapper ? wrapper.Checker.Enter(isWriteAccess) : default;

        /// <summary>
        /// Gets the equality comparer object that is used to determine equality for the values in the set.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static SystemGenerics.IEqualityComparer<T> get_Comparer(SystemGenerics.HashSet<T> instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance.Comparer;
        }

        /// <summary>
        /// Gets the number of elements that are contained in the hash set.
        /// </summary>
#pragma warning disable CA1707 // Identifiers should not contain underscores
#pragma warning disable SA1300 // Element should begin with upper-case letter
#pragma warning disable IDE1006 // Naming Styles
        public static int get_Count(SystemGenerics.HashSet<T> instance)
#pragma warning restore IDE1006 // Naming Styles
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore CA1707 // Identifiers should not contain underscores
        {
            using var scope = Enter(instance, false);
            return instance.Count;
        }

        /// <summary>
        /// Adds the specified element to the hash set.
        /// </summary>
        public static bool Add(SystemGenerics.HashSet<T> instance, T item)
        {
            using var scope = Enter(instance, true);
            return instance.Add(item);
        }

        /// <summary>
        /// Removes all elements from a hash set object.
        /// </summary>
        public static void Clear(SystemGenerics.HashSet<T> instance)
        {
            using var scope = Enter(instance, true);
            instance.Clear();
        }

        /// <summary>
        /// Determines whether a hash set object contains the specified element.
        /// </summary>
        public static bool Contains(SystemGenerics.HashSet<T> instance, T item)
        {
            using var scope = Enter(instance, false);
            return instance.Contains(item);
        }

        /// <summary>
        /// Copies the elements of a hash set object to an array.
        /// </summary>
        public static void CopyTo(SystemGenerics.HashSet<T> instance, T[] array)
        {
            using var scope = Enter(instance, false);
            instance.CopyTo(array);
        }

        /// <summary>
        /// Copies the elements of a hash set object to an array, starting at the specified array index.
        /// </summary>
        public static void CopyTo(SystemGenerics.HashSet<T> instance, T[] array, int arrayIndex)
        {
            using var scope = Enter(instance, false);
            instance.CopyTo(array, arrayIndex);
        }

        /// <summary>
        /// Copies the specified number of elements of a hash set object to an array, starting at the specified array index.
        /// </summary>
        public static void CopyTo(SystemGenerics.HashSet<T> instance, T[] array, int arrayIndex, int count)
        {
            using var scope = Enter(instance, false);
            instance.CopyTo(array, arrayIndex, count);
        }

        /// <summary>
        /// Removes all elements in the specified collection from the current hash set object.
        /// </summary>
        public static void ExceptWith(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, true);
            instance.ExceptWith(other);
        }

        /// <summary>
        /// Returns an enumerator that iterates through a hash set object.
        /// </summary>
        public static SystemGenerics.HashSet<T>.Enumerator GetEnumerator(SystemGenerics.HashSet<T> instance)
        {
            using var scope = Enter(instance, false);
            return instance.GetEnumerator();
        }

        /// <summary>
        /// Implements the <see cref="ISerializable"/> interface and returns the data needed to
        /// serialize a hash set object.
        /// </summary>
        #if NET8_0_OR_GREATER
        [Obsolete("Marking obsolete", DiagnosticId = "SYSLIB0051")]
        #endif
        public static void GetObjectData(SystemGenerics.HashSet<T> instance, SerializationInfo info,
            StreamingContext context)
        {
            using var scope = Enter(instance, false);
            instance.GetObjectData(info, context);
        }

        /// <summary>
        /// Modifies the current hash set object to contain only elements that are present
        /// in that object and in the specified collection.
        /// </summary>
        public static void IntersectWith(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, true);
            instance.IntersectWith(other);
        }

        /// <summary>
        /// Determines whether a hash set object is a proper subset of the specified collection.
        /// </summary>
        public static bool IsProperSubsetOf(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.IsProperSubsetOf(other);
        }

        /// <summary>
        /// Determines whether a hash set object is a proper superset of the specified collection.
        /// </summary>
        public static bool IsProperSupersetOf(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.IsProperSupersetOf(other);
        }

        /// <summary>
        /// Determines whether a hash set object is a subset of the specified collection.
        /// </summary>
        public static bool IsSubsetOf(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.IsSubsetOf(other);
        }

        /// <summary>
        /// Determines whether a hash set object is a superset of the specified collection.
        /// </summary>
        public static bool IsSupersetOf(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.IsSupersetOf(other);
        }

        // TODO: Is this requried?

        /// <summary>
        /// Implements the <see cref="ISerializable"/> interface and raises the deserialization
        /// event when the deserialization is complete.
        /// </summary>
        public static void OnDeserialization(SystemGenerics.HashSet<T> instance, object sender)
        {
            using var scope = Enter(instance, false);
            instance.OnDeserialization(sender);
        }

        /// <summary>
        /// Determines whether a hash set object and a specified collection share common elements.
        /// </summary>
        public static bool Overlaps(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.Overlaps(other);
        }

        /// <summary>
        /// Removes the specified element from a hash set object.
        /// </summary>
        public static bool Remove(SystemGenerics.HashSet<T> instance, T item)
        {
            using var scope = Enter(instance, true);
            return instance.Remove(item);
        }

        /// <summary>
        /// Removes the specified element from a hash set object.
        /// </summary>
        public static int RemoveWhere(SystemGenerics.HashSet<T> instance, Predicate<T> match)
        {
            using var scope = Enter(instance, true);
            return instance.RemoveWhere(match);
        }

        /// <summary>
        /// Determines whether a hash set object and the specified collection contain the same elements.
        /// </summary>
        public static bool SetEquals(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, false);
            return instance.SetEquals(other);
        }

        /// <summary>
        /// Modifies the current hash set object to contain only elements that are present either in
        /// that object or in the specified collection, but not both.
        /// </summary>
        public static void SymmetricExceptWith(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, true);
            instance.SymmetricExceptWith(other);
        }

        /// <summary>
        /// Sets the capacity of a hash set object to the actual number of elements it
        /// contains, rounded up to a nearby, implementation-specific value.
        /// </summary>
        public static void TrimExcess(SystemGenerics.HashSet<T> instance)
        {
            using var scope = Enter(instance, false);
            instance.TrimExcess();
        }

        /// <summary>
        /// Modifies the current hash set object to contain all elements that are
        /// present in itself, the specified collection, or both.
        /// </summary>
        public static void UnionWith(SystemGenerics.HashSet<T> instance, SystemGenerics.IEnumerable<T> other)
        {
            using var scope = Enter(instance, true);
            instance.UnionWith(other);
        }

#if NET
        /// <summary>
        /// Ensures that this hash set object can hold the specified number of elements without growing.
        /// </summary>
        public static int EnsureCapacity(SystemGenerics.HashSet<T> instance, int capacity)
        {
            using var scope = Enter(instance, false);
            return instance.EnsureCapacity(capacity);
        }

        /// <summary>
        /// Searches the set for a given value and returns the equal value it finds, if any.
        /// </summary>
        public static bool TryGetValue(SystemGenerics.HashSet<T> instance, T equalValue, out T actualValue)
        {
            using var scope = Enter(instance, false);
            return instance.TryGetValue(equalValue, out actualValue);
        }
#endif

        /// <summary>
        /// Wraps a hash set so that it can be controlled during testing.
        /// </summary>
        private class Wrapper : SystemGenerics.HashSet<T>
        {
            /// <summary>
            /// Detects unsynchronized concurrent access to this hash set.
            /// </summary>
            internal readonly DataRaceChecker Checker =
                new DataRaceChecker(typeof(SystemGenerics.HashSet<T>));

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
            internal Wrapper(SystemGenerics.IEnumerable<T> collection)
                : base(collection)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IEnumerable<T> collection, SystemGenerics.IEqualityComparer<T> comparer)
                : base(collection, comparer)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            internal Wrapper(SystemGenerics.IEqualityComparer<T> comparer)
                : base(comparer)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Wrapper"/> class.
            /// </summary>
            #if NET8_0_OR_GREATER
            [Obsolete("Marking obsolete", DiagnosticId = "SYSLIB0051")]
            #endif
            internal Wrapper(SerializationInfo info, StreamingContext context)
                : base(info, context)
            {
            }

#if NET
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
            internal Wrapper(int capacity, SystemGenerics.IEqualityComparer<T> comparer)
                : base(capacity, comparer)
            {
            }
#endif

        }
    }
#pragma warning restore CA1000 // Do not declare static members on generic types
}
