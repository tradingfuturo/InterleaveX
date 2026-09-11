// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Rewriting.Types
{
    /// <summary>
    /// Provides the controlled implementation of <see cref="System.Guid.NewGuid"/>.
    /// </summary>
    /// <remarks>This type is intended for compiler use rather than use directly.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class GuidProvider
    {
        private static readonly ConditionalWeakTable<CoyoteRuntime, State> States =
            new ConditionalWeakTable<CoyoteRuntime, State>();

        /// <summary>
        /// Returns a version 4, RFC 4122 variant GUID, deterministically scoped to the current
        /// Coyote runtime. Calls made outside a controlled runtime retain ordinary .NET behavior.
        /// </summary>
        public static System.Guid NewGuid()
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.None)
            {
                return System.Guid.NewGuid();
            }

            State state = States.GetValue(runtime, _ => new State());
            lock (state.SyncObject)
            {
                state.NextValue = checked(state.NextValue + 1);
                return CreateGuid(state.NextValue);
            }
        }

        private static System.Guid CreateGuid(ulong value)
        {
            var bytes = new byte[16];
            for (int i = 0; i < sizeof(ulong); i++)
            {
                bytes[i < 4 ? i : i + 6] = (byte)(value >> (i * 8));
            }

            // Keep the counter clear of the version and variant bits so no bits of its
            // uniqueness are overwritten. Exhaustion fails rather than wrapping.
            bytes[8] = 0xA5;
            bytes[9] = 0x5A;
            bytes[14] = 0xF0;
            bytes[15] = 0x0F;
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new System.Guid(bytes);
        }

        private sealed class State
        {
            internal readonly object SyncObject = new object();
            internal ulong NextValue;
        }
    }
}
