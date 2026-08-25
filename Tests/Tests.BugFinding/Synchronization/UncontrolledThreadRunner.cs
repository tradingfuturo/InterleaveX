// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Threading;
using Microsoft.Coyote.Rewriting;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Starts a real CLR thread without rewriting its start/join machinery. The execution context is
    /// deliberately allowed to flow so the callback sees the live Coyote runtime, but it has no
    /// <c>ControlledOperation</c> on its physical thread.
    /// </summary>
    [SkipRewriting("The callback must execute on a CLR thread that is not a controlled operation.")]
    internal sealed class UncontrolledThreadRunner
    {
        private readonly Thread Thread;

        private Exception Failure;

        internal bool IsCompleted => !this.Thread.IsAlive;

        private UncontrolledThreadRunner(Action action)
        {
            this.Thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    this.Failure = ex;
                }
            })
            {
                IsBackground = true
            };
            this.Thread.Start();
        }

        internal static UncontrolledThreadRunner Start(Action action) => new UncontrolledThreadRunner(action);

        /// <summary>
        /// Waits a bounded amount of real time, preventing a red regression from leaving a host thread alive.
        /// </summary>
        internal void Join()
        {
            if (!this.Thread.Join(millisecondsTimeout: 2000))
            {
                throw new TimeoutException("An uncontrolled synchronization test thread did not complete.");
            }
        }

        internal void ThrowIfFailed()
        {
            if (this.Failure != null)
            {
                throw new InvalidOperationException("The uncontrolled synchronization thread failed.", this.Failure);
            }
        }
    }
}
