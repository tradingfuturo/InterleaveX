// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.IO;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Decides whether a worker process of a parallel run should stop at its next iteration boundary.
    /// </summary>
    /// <remarks>
    /// Consulted once per testing iteration, which for a fast test is once every fraction of a
    /// millisecond, so the checks it stands in front of are throttled: probing the file system and,
    /// worse, taking a snapshot of the process table on every iteration costs more than the iteration
    /// itself. The signals this watches are both minutes-scale events answered by a ten second grace
    /// period, so a probe interval measured in milliseconds loses nothing.
    ///
    /// Split out from the orchestration that consumes it, and parameterized over its clock and its two
    /// probes, so that the throttling can be unit tested without real time or real processes.
    /// </remarks>
    internal sealed class ParallelWorkerStopProbe
    {
        /// <summary>
        /// How long to wait between probes, in milliseconds.
        /// </summary>
        internal const long DefaultProbeIntervalMs = 250;

        /// <summary>
        /// Returns true once the coordinator has asked the workers to stop.
        /// </summary>
        private readonly Func<bool> IsStopRequested;

        /// <summary>
        /// Returns false once the coordinator this worker belongs to is gone.
        /// </summary>
        private readonly Func<bool> IsParentAlive;

        /// <summary>
        /// Returns the number of milliseconds elapsed since an arbitrary fixed point.
        /// </summary>
        private readonly Func<long> ElapsedMs;

        /// <summary>
        /// How long to wait between probes, in milliseconds.
        /// </summary>
        private readonly long IntervalMs;

        /// <summary>
        /// The time of the next probe, or null if no probe has happened yet.
        /// </summary>
        private long? NextProbeMs;

        /// <summary>
        /// Set once a probe has decided that this worker must stop. The decision is sticky: the
        /// signals it is based on never revert, and the caller stops the engine rather than pausing it.
        /// </summary>
        private bool IsStopped;

        /// <summary>
        /// Initializes a new instance of the <see cref="ParallelWorkerStopProbe"/> class.
        /// </summary>
        internal ParallelWorkerStopProbe(Func<bool> isStopRequested, Func<bool> isParentAlive,
            Func<long> elapsedMs, long intervalMs)
        {
            this.IsStopRequested = isStopRequested;
            this.IsParentAlive = isParentAlive;
            this.ElapsedMs = elapsedMs;
            this.IntervalMs = intervalMs;
        }

        /// <summary>
        /// Returns a probe for the specified stop file and coordinator process id.
        /// </summary>
        /// <remarks>
        /// The coordinator's <see cref="Process"/> is resolved once and kept, rather than looked up per
        /// probe. Looking it up by id enumerates the process table, and is also wrong once the
        /// coordinator has exited and its id has been reused by an unrelated process, which reports a
        /// dead coordinator as alive and orphans this worker for the rest of the run. Holding the
        /// handle both makes each probe a handle test and keeps the id from being reused at all.
        /// </remarks>
        internal static ParallelWorkerStopProbe Create(string stopFile, int parentProcessId) =>
            new ParallelWorkerStopProbe(() => File.Exists(stopFile), GetParentLivenessProbe(parentProcessId),
                GetElapsedMillisecondsProbe(), DefaultProbeIntervalMs);

        /// <summary>
        /// Returns true if this worker should stop at the current iteration boundary.
        /// </summary>
        internal bool ShouldStop()
        {
            if (this.IsStopped)
            {
                return true;
            }

            // Probe on the first call whatever the clock says, so that a stop file that was already
            // there, or a coordinator that is already gone, is honored at the first boundary rather
            // than one interval into the run.
            long now = this.ElapsedMs();
            if (this.NextProbeMs.HasValue && now < this.NextProbeMs.Value)
            {
                return false;
            }

            this.NextProbeMs = now + this.IntervalMs;
            this.IsStopped = this.IsStopRequested() || !this.IsParentAlive();
            return this.IsStopped;
        }

        /// <summary>
        /// Returns a probe that reports whether the specified process is still running, or one that
        /// always reports true if there is no such process to watch.
        /// </summary>
        private static Func<bool> GetParentLivenessProbe(int parentProcessId)
        {
            if (parentProcessId is 0)
            {
                // This process was not told which coordinator it belongs to, so it has nothing to
                // outlive and only the stop file governs it.
                return () => true;
            }

            Process parent;
            try
            {
                parent = Process.GetProcessById(parentProcessId);
            }
            catch (Exception)
            {
                // The coordinator is already gone, or cannot be opened at all. Either way this worker
                // has nothing to report to, so it should stop at its first boundary.
                return () => false;
            }

            return () =>
            {
                try
                {
                    return !parent.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            };
        }

        /// <summary>
        /// Returns a probe that reports the milliseconds elapsed since it was created.
        /// </summary>
        private static Func<long> GetElapsedMillisecondsProbe()
        {
            var stopwatch = Stopwatch.StartNew();
            return () => stopwatch.ElapsedMilliseconds;
        }
    }
}
