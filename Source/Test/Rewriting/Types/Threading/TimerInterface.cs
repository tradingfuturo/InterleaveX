// Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Licensed under the GNU General Public License v3.0 or later.

#if NET8_0_OR_GREATER
using System;
using SystemITimer = System.Threading.ITimer;
using SystemTimer = System.Threading.Timer;

namespace Microsoft.Coyote.Rewriting.Types.Threading
{
    /// <summary>
    /// Routes calls made through <see cref="SystemITimer"/> on a controlled <see cref="SystemTimer"/> to its model.
    /// </summary>
    /// <remarks>
    /// This type is intended for compiler use rather than use directly in code. <see cref="SystemTimer"/> implements the
    /// interface, and a change made through it would otherwise reach the unarmed framework timer rather than the
    /// schedule the model owns. Disposal through the interface is routed by the disposable routers.
    /// </remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TimerInterface
    {
        /// <summary>
        /// Changes the start time and the interval between callbacks.
        /// </summary>
        public static bool Change(SystemITimer instance, TimeSpan dueTime, TimeSpan period) =>
            instance is SystemTimer timer ? Timer.Change(timer, dueTime, period) : instance.Change(dueTime, period);
    }
}
#endif
