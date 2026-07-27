// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

#if NET
using System.Collections.Generic;
using SystemCancellationToken = System.Threading.CancellationToken;
using SystemCompiler = System.Runtime.CompilerServices;
using SystemTasks = System.Threading.Tasks;
using SystemValueTask = System.Threading.Tasks.ValueTask;

namespace Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices
{
    /// <summary>
    /// Provides an awaitable async enumerable that is the outcome of invoking
    /// <c>WithCancellation</c> or <c>ConfigureAwait</c> on an <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use only.
    /// <para>
    /// It exists so that the awaitables an <c>await foreach</c> consumes are produced by controlled
    /// code. The rewriter retypes every local, field and call that mentions a known compiler type,
    /// which only stays sound while every producer of such a type is rewritten too. The BCL
    /// <see cref="SystemCompiler.ConfiguredCancelableAsyncEnumerable{T}"/> is not rewritten, so its
    /// <c>MoveNextAsync</c> and <c>DisposeAsync</c> would hand a real
    /// <see cref="ConfiguredValueTaskAwaitable{TResult}"/> to a call site expecting the controlled one,
    /// and the reinterpreted struct would corrupt memory. Mirroring the type here keeps the whole
    /// chain controlled: the awaitables below are built from the controlled awaitable types.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public readonly struct ConfiguredCancelableAsyncEnumerable<T>
    {
        /// <summary>
        /// The enumerable being enumerated.
        /// </summary>
        private readonly IAsyncEnumerable<T> Enumerable;

        /// <summary>
        /// The token that cancels the enumeration.
        /// </summary>
        private readonly SystemCancellationToken CancellationToken;

        /// <summary>
        /// True if continuations resume on the captured context, else false.
        /// </summary>
        private readonly bool ContinueOnCapturedContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfiguredCancelableAsyncEnumerable{T}"/> struct.
        /// </summary>
        internal ConfiguredCancelableAsyncEnumerable(IAsyncEnumerable<T> enumerable,
            bool continueOnCapturedContext, SystemCancellationToken cancellationToken)
        {
            this.Enumerable = enumerable;
            this.ContinueOnCapturedContext = continueOnCapturedContext;
            this.CancellationToken = cancellationToken;
        }

        /// <summary>
        /// Configures how awaits on the tasks returned from the enumeration are performed.
        /// </summary>
        public ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait(bool continueOnCapturedContext) =>
            new ConfiguredCancelableAsyncEnumerable<T>(this.Enumerable, continueOnCapturedContext,
                this.CancellationToken);

        /// <summary>
        /// Sets the token to pass to the enumerator.
        /// </summary>
        public ConfiguredCancelableAsyncEnumerable<T> WithCancellation(SystemCancellationToken cancellationToken) =>
            new ConfiguredCancelableAsyncEnumerable<T>(this.Enumerable, this.ContinueOnCapturedContext,
                cancellationToken);

        /// <summary>
        /// Returns an enumerator that iterates asynchronously through the collection.
        /// </summary>
        public Enumerator GetAsyncEnumerator() =>
            new Enumerator(this.Enumerable.GetAsyncEnumerator(this.CancellationToken),
                this.ContinueOnCapturedContext);

        /// <summary>
        /// Provides an awaitable async enumerator that enables cancelable iteration and configured awaits.
        /// </summary>
        /// <remarks>This type is intended for compiler use only.</remarks>
        public readonly struct Enumerator
        {
            /// <summary>
            /// The enumerator being iterated.
            /// </summary>
            private readonly IAsyncEnumerator<T> AsyncEnumerator;

            /// <summary>
            /// True if continuations resume on the captured context, else false.
            /// </summary>
            private readonly bool ContinueOnCapturedContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="Enumerator"/> struct.
            /// </summary>
            internal Enumerator(IAsyncEnumerator<T> asyncEnumerator, bool continueOnCapturedContext)
            {
                this.AsyncEnumerator = asyncEnumerator;
                this.ContinueOnCapturedContext = continueOnCapturedContext;
            }

            /// <summary>
            /// Gets the element in the collection at the current position of the enumerator.
            /// </summary>
            public T Current => this.AsyncEnumerator.Current;

            /// <summary>
            /// Advances the enumerator asynchronously to the next element of the collection.
            /// </summary>
            public ConfiguredValueTaskAwaitable<bool> MoveNextAsync()
            {
                SystemTasks.ValueTask<bool> task = this.AsyncEnumerator.MoveNextAsync();
                return new ConfiguredValueTaskAwaitable<bool>(ref task, this.ContinueOnCapturedContext);
            }

            /// <summary>
            /// Performs application-defined tasks associated with freeing, releasing or resetting
            /// unmanaged resources asynchronously.
            /// </summary>
            public ConfiguredValueTaskAwaitable DisposeAsync()
            {
                SystemValueTask task = this.AsyncEnumerator.DisposeAsync();
                return new ConfiguredValueTaskAwaitable(ref task, this.ContinueOnCapturedContext);
            }
        }
    }
}
#endif
