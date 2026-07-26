// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using Microsoft.Coyote.Runtime;
using SystemChannels = System.Threading.Channels;

namespace Microsoft.Coyote.Rewriting.Types.Threading.Channels
{
    /// <summary>
    /// Provides static methods for creating channels that can be controlled during testing.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code. The rewriter redirects the
    /// static <see cref="SystemChannels.Channel"/> factory methods here; the returned
    /// <see cref="ControlledChannel{T}"/> derives from the real abstract <c>Channel&lt;T&gt;</c>, so every
    /// subsequent reader/writer call dispatches virtually into controlled code without needing a mock per
    /// reader/writer type.
    /// <para>
    /// Two factory shapes are intentionally NOT redirected (they keep the real, uncontrolled BCL
    /// implementation): channels created inside a static constructor (the rewriter does not rewrite
    /// <c>.cctor</c> bodies — e.g. a channel in a static field initializer), and
    /// <c>CreateUnboundedPrioritized</c> (unused by the systems under test). Both continue to work; they are
    /// simply not observed by the scheduler.
    /// </para>
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:TypeNamesShouldNotMatchNamespaces",
        Justification = "The name must mirror System.Threading.Channels.Channel for the rewriter mapping; " +
        "the only collision is with an unrelated Microsoft.ApplicationInsights.Channel telemetry namespace.")]
    public static class Channel
    {
        /// <summary>
        /// Returns whether channel operations should be controlled, and if so yields a controlled channel
        /// with the specified bounds. The defaults describe an unbounded channel.
        /// </summary>
        private static bool TryCreateControlled<T>(out SystemChannels.Channel<T> channel,
            int capacity = int.MaxValue,
            SystemChannels.BoundedChannelFullMode fullMode = SystemChannels.BoundedChannelFullMode.Wait,
            Action<T> itemDropped = null)
        {
            CoyoteRuntime runtime = CoyoteRuntime.Current;
            if (runtime.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                channel = new ControlledChannel<T>(runtime, capacity, fullMode, itemDropped);
                return true;
            }

            channel = null;
            return false;
        }

        /// <summary>
        /// Creates an unbounded channel usable by any number of readers and writers concurrently.
        /// </summary>
        public static SystemChannels.Channel<T> CreateUnbounded<T>() =>
            TryCreateControlled(out SystemChannels.Channel<T> channel) ? channel :
            SystemChannels.Channel.CreateUnbounded<T>();

        /// <summary>
        /// Creates an unbounded channel subject to the provided options.
        /// </summary>
        public static SystemChannels.Channel<T> CreateUnbounded<T>(SystemChannels.UnboundedChannelOptions options) =>
            TryCreateControlled(out SystemChannels.Channel<T> channel) ? channel :
            SystemChannels.Channel.CreateUnbounded<T>(options);

        /// <summary>
        /// Creates a channel with the specified maximum capacity.
        /// </summary>
        public static SystemChannels.Channel<T> CreateBounded<T>(int capacity) =>
            TryCreateControlled(out SystemChannels.Channel<T> channel, capacity) ? channel :
            SystemChannels.Channel.CreateBounded<T>(capacity);

        /// <summary>
        /// Creates a channel subject to the provided options.
        /// </summary>
        public static SystemChannels.Channel<T> CreateBounded<T>(SystemChannels.BoundedChannelOptions options) =>
            CreateBounded<T>(options, itemDropped: null);

        /// <summary>
        /// Creates a channel subject to the provided options, invoking <paramref name="itemDropped"/> for each
        /// item dropped from the channel's buffer under a non-<c>Wait</c> full mode.
        /// </summary>
        /// <remarks>
        /// A null <paramref name="options"/> is handed to the BCL rather than dereferenced here, so that the
        /// caller still gets its <see cref="ArgumentNullException"/> instead of a <see cref="NullReferenceException"/>.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateBounded<T>(SystemChannels.BoundedChannelOptions options,
            Action<T> itemDropped) =>
            options != null &&
            TryCreateControlled(out SystemChannels.Channel<T> channel, options.Capacity, options.FullMode, itemDropped) ?
            channel : SystemChannels.Channel.CreateBounded<T>(options, itemDropped);
    }
}
#endif
