// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// Represents a group of controlled operations that can be scheduled together during testing.
    /// </summary>
    internal class OperationGroup : IEnumerable<ControlledOperation>, IEquatable<OperationGroup>
    {
        /// <summary>
        /// The unique id of this group.
        /// </summary>
        internal readonly Guid Id;

        /// <summary>
        /// The controlled operation that owns this group.
        /// </summary>
        internal readonly ControlledOperation Owner;

        /// <summary>
        /// The controlled operations that are members of this group.
        /// </summary>
        private readonly HashSet<ControlledOperation> Members;

        /// <summary>
        /// Initializes a new instance of the <see cref="OperationGroup"/> class.
        /// </summary>
        private OperationGroup(Guid? id, ControlledOperation owner)
        {
            this.Id = id ?? Guid.NewGuid();
            this.Owner = owner;
            this.Members = new HashSet<ControlledOperation>();
        }

        /// <summary>
        /// Creates a new <see cref="OperationGroup"/> instance.
        /// </summary>
        internal static OperationGroup Create(ControlledOperation owner) => Create(null, owner);

        /// <summary>
        /// Creates a new <see cref="OperationGroup"/> instance with the specified id.
        /// </summary>
        internal static OperationGroup Create(Guid? id, ControlledOperation owner) => new OperationGroup(id, owner);

        /// <summary>
        /// Registers the specified operation as a member of this group.
        /// </summary>
        internal void RegisterMember(ControlledOperation member) => this.Members.Add(member);

        /// <summary>
        /// Returns an enumerator that iterates through the members of this group.
        /// </summary>
        public IEnumerator<ControlledOperation> GetEnumerator()
        {
            foreach (ControlledOperation op in this.Members)
            {
                yield return op;
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the members of this group.
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Returns true if the specified operation is a member of this group, else false.
        /// </summary>
        internal bool IsMember(ControlledOperation operation) => this.Members.Contains(operation);

        /// <summary>
        /// Determines whether all members of this group are completed.
        /// </summary>
        /// <remarks>
        /// Iterated directly rather than through LINQ's <c>All</c>, which takes an
        /// <see cref="IEnumerable{T}"/> and so boxes the set's struct enumerator. The prioritization
        /// and delay-bounding strategies call this for every group they track at every scheduling
        /// step, and both are in the default portfolio.
        /// </remarks>
        internal bool IsCompleted()
        {
            foreach (var op in this.Members)
            {
                if (op.Status != OperationStatus.Completed)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// </summary>
        public override bool Equals(object obj)
        {
            if (obj is OperationGroup op)
            {
                return this.Id == op.Id;
            }

            return false;
        }

        /// <summary>
        /// Returns the hash code for this instance.
        /// </summary>
        public override int GetHashCode() => this.Id.GetHashCode();

        /// <summary>
        /// Returns a string that represents the current group id.
        /// </summary>
        public override string ToString() => this.Id.ToString();

        /// <summary>
        /// Indicates whether the specified <see cref="OperationGroup"/> is equal
        /// to the current <see cref="OperationGroup"/>.
        /// </summary>
        public bool Equals(OperationGroup other) => this.Equals((object)other);

        /// <summary>
        /// Indicates whether the specified <see cref="OperationGroup"/> is equal
        /// to the current <see cref="OperationGroup"/>.
        /// </summary>
        bool IEquatable<OperationGroup>.Equals(OperationGroup other) => this.Equals(other);
    }
}
