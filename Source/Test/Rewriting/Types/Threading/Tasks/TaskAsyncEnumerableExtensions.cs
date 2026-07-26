// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using CompilerServices = Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices;
using SystemCancellationToken = System.Threading.CancellationToken;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Tasks
{
    /// <summary>
    /// Provides methods for configuring async enumerables and async disposables that can be
    /// controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code. These are the
    /// producers of <see cref="CompilerServices.ConfiguredCancelableAsyncEnumerable{T}"/> and
    /// <see cref="CompilerServices.ConfiguredAsyncDisposable"/>; rewriting them is what keeps an
    /// <c>await foreach</c> or <c>await using</c> over a configured source running entirely on
    /// controlled awaitable types.
    /// <para>
    /// The <c>ToBlockingEnumerable</c> shape is deliberately not redirected: it yields no awaitable,
    /// so the real one is already consistent with what the rewriter leaves at the call site.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TaskAsyncEnumerableExtensions
    {
        /// <summary>
        /// Configures how awaits on the tasks returned from an async disposable are performed.
        /// </summary>
        public static CompilerServices.ConfiguredAsyncDisposable ConfigureAwait(IAsyncDisposable source,
            bool continueOnCapturedContext) =>
            new CompilerServices.ConfiguredAsyncDisposable(source, continueOnCapturedContext);

        /// <summary>
        /// Configures how awaits on the tasks returned from an async iteration are performed.
        /// </summary>
        public static CompilerServices.ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait<T>(
            IAsyncEnumerable<T> source, bool continueOnCapturedContext) =>
            new CompilerServices.ConfiguredCancelableAsyncEnumerable<T>(source, continueOnCapturedContext,
                default);

        /// <summary>
        /// Sets the token to pass to the enumerator of an async iteration.
        /// </summary>
        public static CompilerServices.ConfiguredCancelableAsyncEnumerable<T> WithCancellation<T>(
            IAsyncEnumerable<T> source, SystemCancellationToken cancellationToken) =>
            new CompilerServices.ConfiguredCancelableAsyncEnumerable<T>(source, true, cancellationToken);
    }
}
#endif
