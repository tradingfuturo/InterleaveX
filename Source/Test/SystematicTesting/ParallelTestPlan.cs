// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.SystematicTesting
{
    /// <summary>
    /// Computes how to shard testing iterations across worker processes, and how to build
    /// the command line for each worker.
    /// </summary>
    /// <remarks>
    /// This is pure logic with no process or file system interaction, so that it can be
    /// unit tested directly. The orchestration that consumes it lives in the command line
    /// tool.
    /// </remarks>
    internal static class ParallelTestPlan
    {
        /// <summary>
        /// Options that are removed from the inherited command line because the coordinator
        /// supplies its own value for each worker, or because they must not reach a worker.
        /// </summary>
        private static readonly string[] StrippedValueOptions =
        {
            "-m", "--method", "-i", "--iterations", "--seed", "-o", "--outdir", "--workers"
        };

        /// <summary>
        /// Options that are removed from the inherited command line and take no value.
        /// </summary>
        /// <remarks>
        /// The parallel option belongs here rather than above because it carries no value of its own.
        /// The token after it is the next argument, and consuming it would launch every worker without
        /// whatever that token was — the assembly path, for 'coyote test --parallel App.dll'.
        /// </remarks>
        private static readonly string[] StrippedFlagOptions =
        {
            "-b", "--break", "--list-tests", "-p", "--parallel"
        };

        /// <summary>
        /// Computes the shards to distribute the configured iterations across.
        /// </summary>
        /// <param name="configuration">The configuration of the run being sharded.</param>
        /// <param name="baseSeed">The seed that the sequential run would have started from.</param>
        /// <param name="requestedWorkers">The number of workers requested by the user.</param>
        internal static IReadOnlyList<Shard> Compute(Configuration configuration, uint baseSeed, uint requestedWorkers)
        {
            int portfolioSize = OperationScheduler.GetPortfolioSize(configuration);
            return configuration.TestingTimeout > 0 ?
                ComputeForTimeout(configuration, baseSeed, requestedWorkers, portfolioSize) :
                ComputeForIterations(configuration.TestingIterations, baseSeed, requestedWorkers, portfolioSize);
        }

        /// <summary>
        /// Computes shards for a run bounded by a number of iterations.
        /// </summary>
        /// <remarks>
        /// The seed ranges are contiguous and pairwise disjoint, and the shard lengths sum
        /// to exactly the requested total, so the union of what the workers explore is the
        /// seed set that the sequential run would have explored. Each shard length except
        /// possibly the last is a multiple of the portfolio size, which keeps every worker's
        /// local iteration index aligned with the portfolio rotation of the sequential run.
        /// </remarks>
        private static IReadOnlyList<Shard> ComputeForIterations(uint iterations, uint baseSeed,
            uint requestedWorkers, int portfolioSize)
        {
            var shards = new List<Shard>();
            if (iterations is 0)
            {
                return shards;
            }

            uint p = (uint)portfolioSize;

            // Never spawn a worker that would receive fewer iterations than one full
            // rotation of the portfolio, and never more workers than there are iterations.
            uint workers = Math.Min(requestedWorkers, CeilingDivide(iterations, p));
            workers = Math.Max(1, Math.Min(workers, iterations));

            // Round the chunk up to a whole number of portfolio rotations, then recompute
            // the worker count, since rounding up may make a worker unnecessary.
            uint chunk = RoundUpToMultiple(CeilingDivide(iterations, workers), p);
            workers = CeilingDivide(iterations, chunk);

            for (uint idx = 0; idx < workers; idx++)
            {
                uint offset = idx * chunk;
                uint length = Math.Min(chunk, iterations - offset);
                shards.Add(new Shard(idx, unchecked(baseSeed + offset), length));
            }

            return shards;
        }

        /// <summary>
        /// Computes shards for a run bounded by a timeout rather than a number of iterations.
        /// </summary>
        /// <remarks>
        /// The iteration count is effectively unbounded here, so a count based split is
        /// meaningless. Give each worker a widely separated seed origin instead. The stride
        /// is a multiple of the portfolio size so that portfolio alignment is preserved, and
        /// is large enough that two workers would have to complete hundreds of millions of
        /// iterations before their seed ranges could meet.
        /// </remarks>
        private static IReadOnlyList<Shard> ComputeForTimeout(Configuration configuration, uint baseSeed,
            uint requestedWorkers, int portfolioSize)
        {
            var shards = new List<Shard>();
            uint workers = Math.Max(1, requestedWorkers);
            uint stride = RoundDownToMultiple(uint.MaxValue / workers, (uint)portfolioSize);
            for (uint idx = 0; idx < workers; idx++)
            {
                shards.Add(new Shard(idx, unchecked(baseSeed + (idx * stride)), configuration.TestingIterations));
            }

            return shards;
        }

        /// <summary>
        /// Builds the command line for the specified shard, by filtering the command line
        /// this process was invoked with and appending the per worker overrides.
        /// </summary>
        /// <remarks>
        /// The original arguments are filtered rather than reconstructed from the
        /// configuration, so that options this method does not know about are still passed
        /// through. Recursion is impossible because the parallel option is always stripped
        /// and never re-added, so a worker always takes the sequential path.
        /// </remarks>
        internal static string[] BuildChildArgs(IReadOnlyList<string> originalArgs, string method,
            Shard shard, string outputDirectory)
        {
            var args = new List<string>(originalArgs.Count + 8);
            for (int idx = 0; idx < originalArgs.Count; idx++)
            {
                string arg = originalArgs[idx];
                string name = GetOptionName(arg);
                if (Array.IndexOf(StrippedFlagOptions, name) >= 0)
                {
                    continue;
                }

                if (Array.IndexOf(StrippedValueOptions, name) >= 0)
                {
                    // Consume the value too, unless it was attached with '=' or ':', or the
                    // next token is itself an option rather than this option's value.
                    if (!HasAttachedValue(arg) && idx + 1 < originalArgs.Count &&
                        !IsOptionToken(originalArgs[idx + 1]))
                    {
                        idx++;
                    }

                    continue;
                }

                args.Add(arg);
            }

            args.Add("-m");
            args.Add(method);
            args.Add("-i");
            args.Add(shard.Iterations.ToString(CultureInfo.InvariantCulture));
            args.Add("--seed");
            args.Add(shard.Seed.ToString(CultureInfo.InvariantCulture));
            args.Add("-o");
            args.Add(outputDirectory);
            return args.ToArray();
        }

        /// <summary>
        /// Returns the option name of the specified argument, stripped of any attached
        /// value, or the argument itself if it is not an option.
        /// </summary>
        private static string GetOptionName(string arg)
        {
            if (!IsOptionToken(arg))
            {
                return arg;
            }

            int separator = arg.IndexOfAny(new[] { '=', ':' });
            return separator < 0 ? arg : arg.Substring(0, separator);
        }

        /// <summary>
        /// Returns true if the specified argument carries its value inline.
        /// </summary>
        private static bool HasAttachedValue(string arg) => IsOptionToken(arg) && arg.IndexOfAny(new[] { '=', ':' }) >= 0;

        /// <summary>
        /// Returns true if the specified argument is an option rather than a value. A bare
        /// negative number is a value, not an option.
        /// </summary>
        private static bool IsOptionToken(string arg) =>
            arg.Length > 1 && arg[0] is '-' && !(arg[1] >= '0' && arg[1] <= '9');

        /// <summary>
        /// Divides rounding towards positive infinity.
        /// </summary>
        /// <remarks>
        /// The dividend is decremented before the division rather than the divisor added to it, so that
        /// a dividend near <see cref="uint.MaxValue"/> cannot wrap. Adding first wraps to a tiny value
        /// and yields zero, which then divides by zero one step later.
        /// </remarks>
        private static uint CeilingDivide(uint dividend, uint divisor) =>
            dividend is 0 ? 0 : ((dividend - 1) / divisor) + 1;

        /// <summary>
        /// Rounds the specified value up to the nearest multiple of the specified factor, saturating at
        /// <see cref="uint.MaxValue"/>.
        /// </summary>
        /// <remarks>
        /// The rounded up value can exceed <see cref="uint.MaxValue"/>, and a wrapped chunk size would
        /// hand the workers overlapping or empty seed ranges. It saturates at the maximum rather than at
        /// the largest multiple that fits, because the caller divides the iteration count by this to
        /// recompute the worker count: the largest multiple can be smaller than the value it was asked
        /// to round up, which then needs one more worker than was requested to cover the run. Giving up
        /// the alignment of a chunk this size costs nothing, since it can only ever be the last one.
        /// </remarks>
        private static uint RoundUpToMultiple(uint value, uint factor)
        {
            uint multiples = CeilingDivide(value, factor);
            return multiples > uint.MaxValue / factor ? uint.MaxValue : multiples * factor;
        }

        /// <summary>
        /// Rounds the specified value down to the nearest non-zero multiple of the factor.
        /// </summary>
        private static uint RoundDownToMultiple(uint value, uint factor) => Math.Max(factor, value / factor * factor);

        /// <summary>
        /// A contiguous range of testing iterations assigned to a single worker process.
        /// </summary>
        internal readonly struct Shard
        {
            /// <summary>
            /// The zero based index of the worker this shard is assigned to.
            /// </summary>
            internal uint Index { get; }

            /// <summary>
            /// The random generator seed the worker starts from.
            /// </summary>
            internal uint Seed { get; }

            /// <summary>
            /// The number of testing iterations the worker runs.
            /// </summary>
            internal uint Iterations { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="Shard"/> struct.
            /// </summary>
            internal Shard(uint index, uint seed, uint iterations)
            {
                this.Index = index;
                this.Seed = seed;
                this.Iterations = iterations;
            }
        }
    }
}
