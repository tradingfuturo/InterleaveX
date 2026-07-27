// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

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
    /// Two shapes keep the real, uncontrolled BCL implementation. A channel created inside a static
    /// constructor is one, because the rewriter does not rewrite <c>.cctor</c> bodies (e.g. a channel in
    /// a static field initializer), so the call is never redirected here and nothing can report it. A
    /// prioritized channel is the other: a priority queue decides the order items come out in, which
    /// <see cref="ControlledChannel{T}"/> does not model. Both continue to work; they are simply not
    /// observed by the scheduler, and the prioritized one says so through
    /// <see cref="CoyoteRuntime.NotifyUncontrolledPrimitive"/> rather than losing the coverage silently.
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
        /// <remarks>
        /// A null <paramref name="options"/> is handed to the BCL rather than ignored here, so that the
        /// caller still gets its <see cref="ArgumentNullException"/>.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateUnbounded<T>(SystemChannels.UnboundedChannelOptions options) =>
            options != null && TryCreateControlled(out SystemChannels.Channel<T> channel) ? channel :
            SystemChannels.Channel.CreateUnbounded<T>(options);

        /// <summary>
        /// The smallest capacity a controlled channel is built for. A capacity of zero asks for the
        /// rendezvous channel, which <see cref="ControlledChannel{T}"/> implements, but only .NET 10
        /// has: every earlier framework rejects it, and controlling it there would turn the caller's
        /// <see cref="ArgumentOutOfRangeException"/> into a working channel.
        /// </summary>
#if NET10_0_OR_GREATER
        private const int MinControlledCapacity = 0;
#else
        private const int MinControlledCapacity = 1;
#endif

        /// <summary>
        /// Creates a channel with the specified maximum capacity.
        /// </summary>
        /// <remarks>
        /// A capacity the framework does not accept is handed to the BCL rather than rejected here, so
        /// that the caller still gets its <see cref="ArgumentOutOfRangeException"/>. Where a capacity of
        /// zero is accepted it is controlled like any other: <see cref="ControlledChannel{T}"/> buffers
        /// nothing and hands each item from a writer to a reader directly.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateBounded<T>(int capacity) =>
            capacity >= MinControlledCapacity &&
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
        /// The capacity carries the same condition as the integer overload. It cannot in fact fail it —
        /// <c>BoundedChannelOptions</c> validates the capacity on the way in, and rejects a zero on every
        /// framework whose channel does not support one — but the check is kept so that the two overloads
        /// cannot drift apart if that ever changes.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateBounded<T>(SystemChannels.BoundedChannelOptions options,
            Action<T> itemDropped) =>
            options != null && options.Capacity >= MinControlledCapacity &&
            TryCreateControlled(out SystemChannels.Channel<T> channel, options.Capacity, options.FullMode, itemDropped) ?
            channel : SystemChannels.Channel.CreateBounded<T>(options, itemDropped);

#if NET10_0_OR_GREATER
        /// <summary>
        /// Creates an unbounded channel that reads items in priority order.
        /// </summary>
        /// <remarks>
        /// Redirected only so that the lost coverage can be reported; the channel returned is the real,
        /// uncontrolled one. A priority queue decides the order items come out in, and
        /// <see cref="ControlledChannel{T}"/> is a FIFO that does not model that.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateUnboundedPrioritized<T>()
        {
            ReportUncontrolledChannel();
            return SystemChannels.Channel.CreateUnboundedPrioritized<T>();
        }

        /// <summary>
        /// Creates an unbounded channel that reads items in priority order, subject to the provided options.
        /// </summary>
        /// <remarks>
        /// Redirected only so that the lost coverage can be reported; see the parameterless overload.
        /// </remarks>
        public static SystemChannels.Channel<T> CreateUnboundedPrioritized<T>(
            SystemChannels.UnboundedPrioritizedChannelOptions<T> options)
        {
            ReportUncontrolledChannel();
            return SystemChannels.Channel.CreateUnboundedPrioritized<T>(options);
        }

        /// <summary>
        /// Reports that a channel this class cannot control was created, so that the interleavings it
        /// hides are not quietly missing from an otherwise green run.
        /// </summary>
        private static void ReportUncontrolledChannel() =>
            CoyoteRuntime.Current.NotifyUncontrolledPrimitive("A prioritized channel");
#endif
    }
}
#endif
