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
        /// Returns whether channel operations should be controlled, yielding the current runtime when so.
        /// </summary>
        private static bool TryGetControlledRuntime(out CoyoteRuntime runtime)
        {
            runtime = CoyoteRuntime.Current;
            return runtime.SchedulingPolicy is SchedulingPolicy.Interleaving;
        }

        /// <summary>
        /// Creates an unbounded channel usable by any number of readers and writers concurrently.
        /// </summary>
        public static SystemChannels.Channel<T> CreateUnbounded<T>()
        {
            if (TryGetControlledRuntime(out var runtime))
            {
                return new ControlledChannel<T>(runtime, capacity: int.MaxValue,
                    fullMode: SystemChannels.BoundedChannelFullMode.Wait, itemDropped: null);
            }

            return SystemChannels.Channel.CreateUnbounded<T>();
        }

        /// <summary>
        /// Creates an unbounded channel subject to the provided options.
        /// </summary>
        public static SystemChannels.Channel<T> CreateUnbounded<T>(SystemChannels.UnboundedChannelOptions options)
        {
            if (TryGetControlledRuntime(out var runtime))
            {
                return new ControlledChannel<T>(runtime, capacity: int.MaxValue,
                    fullMode: SystemChannels.BoundedChannelFullMode.Wait, itemDropped: null);
            }

            return SystemChannels.Channel.CreateUnbounded<T>(options);
        }

        /// <summary>
        /// Creates a channel with the specified maximum capacity.
        /// </summary>
        public static SystemChannels.Channel<T> CreateBounded<T>(int capacity)
        {
            if (TryGetControlledRuntime(out var runtime))
            {
                return new ControlledChannel<T>(runtime, capacity,
                    fullMode: SystemChannels.BoundedChannelFullMode.Wait, itemDropped: null);
            }

            return SystemChannels.Channel.CreateBounded<T>(capacity);
        }

        /// <summary>
        /// Creates a channel subject to the provided options.
        /// </summary>
        public static SystemChannels.Channel<T> CreateBounded<T>(SystemChannels.BoundedChannelOptions options) =>
            CreateBounded<T>(options, itemDropped: null);

        /// <summary>
        /// Creates a channel subject to the provided options, invoking <paramref name="itemDropped"/> for each
        /// item dropped from the channel's buffer under a non-<c>Wait</c> full mode.
        /// </summary>
        public static SystemChannels.Channel<T> CreateBounded<T>(SystemChannels.BoundedChannelOptions options,
            Action<T> itemDropped)
        {
            if (TryGetControlledRuntime(out var runtime))
            {
                return new ControlledChannel<T>(runtime, options.Capacity, options.FullMode, itemDropped);
            }

            return SystemChannels.Channel.CreateBounded<T>(options, itemDropped);
        }
    }
}
#endif
