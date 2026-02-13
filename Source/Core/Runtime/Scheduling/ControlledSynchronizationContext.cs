// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// The synchronization context where controlled operations are executed.
    /// </summary>
    internal sealed class ControlledSynchronizationContext : SynchronizationContext, IDisposable
    {
        /// <summary>
        /// Responsible for controlling the execution of operations during systematic testing.
        /// </summary>
        internal CoyoteRuntime Runtime { get; private set; }

        /// <summary>
        /// The operation scheduling policy used by the runtime.
        /// </summary>
        internal SchedulingPolicy SchedulingPolicy => this.Runtime?.SchedulingPolicy ?? SchedulingPolicy.None;

        /// <summary>
        /// A wrapper synchronization context with a different object identity, used to prevent
        /// the .NET runtime from inlining task continuations. When a task completes, .NET checks
        /// if the captured SynchronizationContext is the same instance as the current one; if so,
        /// it inlines the continuation (bypassing Post). Using this wrapper forces the continuation
        /// through Post, which is necessary for group-preserving continuation scheduling.
        /// </summary>
        private readonly AntiInlineSynchronizationContext AntiInlineContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledSynchronizationContext"/> class.
        /// </summary>
        internal ControlledSynchronizationContext(CoyoteRuntime runtime)
        {
            this.Runtime = runtime;
            this.AntiInlineContext = new AntiInlineSynchronizationContext(this);
        }

        /// <summary>
        /// Returns a SynchronizationContext wrapper with a different object identity.
        /// This prevents .NET from inlining continuations, forcing them through Post.
        /// </summary>
        internal SynchronizationContext GetAntiInlineContext() => this.AntiInlineContext;

        /// <inheritdoc/>
        public override void Post(SendOrPostCallback d, object state)
        {
            try
            {
                this.Runtime?.LogWriter.LogDebug("[coyote::debug] Posting callback from thread '{0}'.",
                    Thread.CurrentThread.ManagedThreadId);
                var group = this.Runtime?.TryGetContinuationGroup(state);
                this.Runtime?.Schedule(() => d(state), group: group);
            }
            catch (ThreadInterruptedException)
            {
                // Ignore the thread interruption.
            }
        }

        /// <inheritdoc/>
        public override SynchronizationContext CreateCopy() => this;

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Runtime = null;
        }

        /// <summary>
        /// A thin wrapper around <see cref="ControlledSynchronizationContext"/> whose sole purpose
        /// is to have a different object identity. This prevents the .NET runtime's inlining
        /// optimization in SynchronizationContextAwaitTaskContinuation.Run, which checks
        /// <c>m_syncContext == SynchronizationContext.Current</c>.
        /// </summary>
        private sealed class AntiInlineSynchronizationContext : SynchronizationContext
        {
            private readonly ControlledSynchronizationContext Inner;

            internal AntiInlineSynchronizationContext(ControlledSynchronizationContext inner)
            {
                this.Inner = inner;
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                this.Inner.Post(d, state);
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                this.Inner.Send(d, state);
            }

            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
