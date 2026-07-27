// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET
using System;
using SystemCompiler = System.Runtime.CompilerServices;
using SystemValueTask = System.Threading.Tasks.ValueTask;

namespace Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices
{
    /// <summary>
    /// Provides a type that is the outcome of invoking <c>ConfigureAwait</c> on an
    /// <see cref="IAsyncDisposable"/>.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use only. It exists for the same reason as
    /// <see cref="ConfiguredCancelableAsyncEnumerable{T}"/>: the BCL
    /// <see cref="SystemCompiler.ConfiguredAsyncDisposable"/> is not rewritten, so the
    /// <c>await using (x.ConfigureAwait(false))</c> of a disposal would hand a real
    /// <see cref="ConfiguredValueTaskAwaitable"/> to a call site the rewriter has already retyped to
    /// the controlled one.
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public readonly struct ConfiguredAsyncDisposable
    {
        /// <summary>
        /// The resource being disposed.
        /// </summary>
        private readonly IAsyncDisposable Source;

        /// <summary>
        /// True if continuations resume on the captured context, else false.
        /// </summary>
        private readonly bool ContinueOnCapturedContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredAsyncDisposable"/> struct.
        /// </summary>
        internal ConfiguredAsyncDisposable(IAsyncDisposable source, bool continueOnCapturedContext)
        {
            this.Source = source;
            this.ContinueOnCapturedContext = continueOnCapturedContext;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing or resetting
        /// unmanaged resources asynchronously.
        /// </summary>
        /// <remarks>
        /// A null source is dereferenced rather than guarded, so that a default-valued instance
        /// throws the same <see cref="NullReferenceException"/> the BCL type throws.
        /// </remarks>
        public ConfiguredValueTaskAwaitable DisposeAsync()
        {
            SystemValueTask task = this.Source.DisposeAsync();
            return new ConfiguredValueTaskAwaitable(ref task, this.ContinueOnCapturedContext);
        }
    }
}
#endif
