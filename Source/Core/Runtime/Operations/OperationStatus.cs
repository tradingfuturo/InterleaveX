// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// The status of a controlled operation.
    /// </summary>
    internal enum OperationStatus
    {
        /// <summary>
        /// The operation does not have a status yet.
        /// </summary>
        None = 0,

        /// <summary>
        /// The operation is enabled.
        /// </summary>
        Enabled,

        /// <summary>
        /// The operation is paused until a dependency is resolved.
        /// </summary>
        Paused,

        /// <summary>
        /// The operation is paused until a delay completes.
        /// </summary>
        PausedOnDelay,

        /// <summary>
        /// The operation is paused until it gets signaled by any awaited resource.
        /// </summary>
        PausedOnAnyResource,

        /// <summary>
        /// The operation is paused until it gets signaled by all awaited resources.
        /// </summary>
        PausedOnAllResources,

        /// <summary>
        /// The operation is paused until it gets signaled by any awaited resource OR its delay completes,
        /// whichever happens first. This is what a resource wait with a finite timeout is: it can be woken
        /// by a signal, and it can also give up on its own.
        /// <para>SELF-RESOLVING, exactly like <see cref="PausedOnDelay"/>: the scheduler decrements its
        /// delay each step and enables it once the delay elapses or nothing else can run. It is therefore
        /// deliberately excluded from the deadlock detector's paused lists — an operation that will
        /// re-enable itself is not deadlocked, and listing it there would report a bug where the real
        /// program would simply have timed out.</para>
        /// </summary>
        PausedOnResourceOrDelay,

        /// <summary>
        /// The operation is paused until receives an event.
        /// </summary>
        PausedOnReceive,

        /// <summary>
        /// The operation is completed.
        /// </summary>
        Completed
    }
}
