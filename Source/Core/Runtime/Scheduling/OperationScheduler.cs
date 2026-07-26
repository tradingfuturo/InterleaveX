// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using Microsoft.Coyote.Coverage;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Testing;
using Microsoft.Coyote.Testing.Fuzzing;
using Microsoft.Coyote.Testing.Interleaving;
using BoundedRandomFuzzingStrategy = Microsoft.Coyote.Testing.Fuzzing.BoundedRandomStrategy;
using DelayBoundingInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.DelayBoundingStrategy;
using DFSInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.DFSStrategy;
using PrioritizationFuzzingStrategy = Microsoft.Coyote.Testing.Fuzzing.PrioritizationStrategy;
using PrioritizationInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.PrioritizationStrategy;
using ProbabilisticRandomInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.ProbabilisticRandomStrategy;
using QLearningInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.QLearningStrategy;
using RandomInterleavingStrategy = Microsoft.Coyote.Testing.Interleaving.RandomStrategy;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// Scheduler that controls the execution of operations during testing.
    /// </summary>
    internal sealed class OperationScheduler
    {
        /// <summary>
        /// The configuration used by the runtime.
        /// </summary>
        private readonly Configuration Configuration;

        /// <summary>
        /// The portfolio of exploration strategies.
        /// </summary>
        private readonly LinkedList<Strategy> Portfolio;

        /// <summary>
        /// The exploration strategy used in the current iteration.
        /// </summary>
        private Strategy Strategy => this.Portfolio.First.Value;

        /// <summary>
        /// The pipeline of schedule reducers.
        /// </summary>
        private readonly List<IScheduleReducer> Reducers;

        /// <summary>
        /// Reusable buffer holding the operations that can be scheduled in the current scheduling
        /// step, after filtering and reduction.
        /// </summary>
        /// <remarks>
        /// This buffer and <see cref="ReducedOperations"/> are swapped as the reducer pipeline runs,
        /// so neither field can be assumed to hold the final result; see <see cref="GetNextOperation"/>.
        /// They are cleared rather than reallocated, and this scheduler outlives the test iterations,
        /// so after the first few scheduling steps the scheduling loop stops allocating altogether.
        /// </remarks>
        private readonly List<ControlledOperation> EnabledOperations;

        /// <summary>
        /// Reusable scratch buffer that receives the output of each schedule reducer.
        /// </summary>
        private readonly List<ControlledOperation> ReducedOperations;

        /// <summary>
        /// Responsible for generating random values.
        /// </summary>
        internal IRandomValueGenerator ValueGenerator { get; private set; }

        /// <summary>
        /// The installed operation scheduling policy.
        /// </summary>
        internal SchedulingPolicy SchedulingPolicy { get; private set; }

        /// <summary>
        /// Directed graph representing the execution as steps (edges) between operations (nodes).
        /// </summary>
        internal readonly ExecutionGraph Graph;

        /// <summary>
        /// The trace explored in the current iteration.
        /// </summary>
        internal readonly ExecutionTrace Trace;

        /// <summary>
        /// The prefix trace, if there is any specified. The scheduler will attempt
        /// to reproduce this trace, before performing any new exploration.
        /// </summary>
        private ExecutionTrace PrefixTrace;

        /// <summary>
        /// The count of exploration steps in the current iteration.
        /// </summary>
        internal int StepCount => this.Strategy.GetStepCount();

        /// <summary>
        /// True if the max number of steps that should be explored has been
        /// reached in the current iteration, else false.
        /// </summary>
        internal bool IsMaxStepsReached => this.Strategy.IsMaxStepsReached();

        /// <summary>
        /// True if the current iteration is fair, else false.
        /// </summary>
        internal bool IsIterationFair => this.Strategy.IsFair;

        /// <summary>
        /// Checks if the scheduler is replaying the schedule trace.
        /// </summary>
        internal bool IsReplaying { get; private set; }

        /// <summary>
        /// Returns the number of exploration strategies that the specified configuration
        /// rotates through, which is 1 when the portfolio is disabled.
        /// </summary>
        /// <remarks>
        /// The portfolio is rotated round-robin in <see cref="InitializeNextIteration"/>, so
        /// an iteration is identified by its seed together with its index modulo this value.
        /// Anything partitioning iterations across processes must keep that alignment. Kept
        /// beside the portfolio construction below so the two cannot drift apart.
        /// </remarks>
        internal static int GetPortfolioSize(Configuration configuration) =>
            !configuration.PortfolioMode.IsEnabled() ? 1 :
            configuration.IsSystematicFuzzingEnabled ? 2 : 5;

        /// <summary>
        /// Initializes a new instance of the <see cref="OperationScheduler"/> class.
        /// </summary>
        private OperationScheduler(Configuration configuration, SchedulingPolicy policy, IRandomValueGenerator generator, ExecutionTrace prefixTrace)
        {
            this.Configuration = configuration;
            this.SchedulingPolicy = policy;
            this.PrefixTrace = prefixTrace;
            this.ValueGenerator = generator;
            this.Graph = ExecutionGraph.Create();
            this.Trace = ExecutionTrace.Create();

            this.Portfolio = new LinkedList<Strategy>();
            this.EnabledOperations = new List<ControlledOperation>();
            this.ReducedOperations = new List<ControlledOperation>();
            this.Reducers = new List<IScheduleReducer>();
            if (configuration.IsExecutionTraceCycleReductionEnabled)
            {
                this.Reducers.Add(new TraceCycleReducer());
            }

            if (configuration.IsPartialOrderSamplingEnabled)
            {
                this.Reducers.Add(new PartialOrderReducer());
            }

            this.IsReplaying = this.SchedulingPolicy is SchedulingPolicy.Interleaving && prefixTrace.Length > 0;
            if (!configuration.UserExplicitlySetLivenessTemperatureThreshold &&
                configuration.MaxFairSchedulingSteps > 0)
            {
                configuration.LivenessTemperatureThreshold = configuration.MaxFairSchedulingSteps / 2;
            }

            // Portfolio mode works with both interleaving and fuzzing exploration strategies, but not during replay.
            if (this.Configuration.PortfolioMode.IsEnabled() && !this.IsReplaying)
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    bool isFair = this.Configuration.PortfolioMode.IsFair();
                    this.Portfolio.AddLast(new RandomInterleavingStrategy(configuration));
                    this.Portfolio.AddLast(new ProbabilisticRandomInterleavingStrategy(configuration, 3));
                    this.Portfolio.AddLast(new PrioritizationInterleavingStrategy(configuration, 10, isFair));
                    this.Portfolio.AddLast(new DelayBoundingInterleavingStrategy(configuration, 10, isFair));
                    this.Portfolio.AddLast(new QLearningInterleavingStrategy(configuration));
                    configuration.IsImplicitProgramStateHashingEnabled = true;
                }
                else if (this.SchedulingPolicy is SchedulingPolicy.Fuzzing)
                {
                    // Set a default strategy bound for the fuzzing prioritization strategy,
                    // which uses it to determine reshuffling frequency. This matches the
                    // default bound used by the CLI when prioritization is explicitly selected.
                    if (configuration.StrategyBound is 0)
                    {
                        configuration.StrategyBound = 10;
                    }

                    this.Portfolio.AddLast(new BoundedRandomFuzzingStrategy(configuration));
                    this.Portfolio.AddLast(new PrioritizationFuzzingStrategy(configuration));
                }
            }
            else
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    switch (configuration.ExplorationStrategy)
                    {
                        case ExplorationStrategy.Probabilistic:
                            this.Portfolio.AddLast(new ProbabilisticRandomInterleavingStrategy(configuration, configuration.StrategyBound));
                            break;
                        case ExplorationStrategy.Prioritization:
                            this.Portfolio.AddLast(new PrioritizationInterleavingStrategy(configuration, configuration.StrategyBound, false));
                            break;
                        case ExplorationStrategy.FairPrioritization:
                            this.Portfolio.AddLast(new PrioritizationInterleavingStrategy(configuration, configuration.StrategyBound, true));
                            break;
                        case ExplorationStrategy.DelayBounding:
                            this.Portfolio.AddLast(new DelayBoundingInterleavingStrategy(configuration, configuration.StrategyBound, false));
                            break;
                        case ExplorationStrategy.FairDelayBounding:
                            this.Portfolio.AddLast(new DelayBoundingInterleavingStrategy(configuration, configuration.StrategyBound, true));
                            break;
                        case ExplorationStrategy.QLearning:
                            this.Portfolio.AddLast(new QLearningInterleavingStrategy(configuration));
                            break;
                        case ExplorationStrategy.DFS:
                            this.Portfolio.AddLast(new DFSInterleavingStrategy(configuration));
                            break;
                        case ExplorationStrategy.Random:
                        default:
                            this.Portfolio.AddLast(new RandomInterleavingStrategy(configuration));
                            break;
                    }
                }
                else if (this.SchedulingPolicy is SchedulingPolicy.Fuzzing)
                {
                    switch (configuration.ExplorationStrategy)
                    {
                        case ExplorationStrategy.Prioritization:
                            this.Portfolio.AddLast(new PrioritizationFuzzingStrategy(configuration));
                            break;
                        case ExplorationStrategy.Random:
                        default:
                            this.Portfolio.AddLast(new BoundedRandomFuzzingStrategy(configuration));
                            break;
                    }
                }
            }

            // Setup all instantiated exploration strategies with additional features.
            foreach (var strategy in this.Portfolio)
            {
                strategy.RandomValueGenerator = generator;
                if (strategy is InterleavingStrategy interleavingStrategy)
                {
                    interleavingStrategy.TracePrefix = prefixTrace;
                }
            }
        }

        /// <summary>
        /// Creates a new instance of the <see cref="OperationScheduler"/> class.
        /// </summary>
        internal static OperationScheduler Setup(Configuration configuration, ExecutionTrace prefixTrace) =>
            new OperationScheduler(configuration,
                configuration.IsSystematicFuzzingEnabled ? SchedulingPolicy.Fuzzing : SchedulingPolicy.Interleaving,
                new RandomValueGenerator(configuration), prefixTrace);

        /// <summary>
        /// Creates a new instance of the <see cref="OperationScheduler"/> class.
        /// </summary>
        internal static OperationScheduler Setup(Configuration configuration, SchedulingPolicy policy,
            IRandomValueGenerator valueGenerator) =>
            new OperationScheduler(configuration, policy, valueGenerator, ExecutionTrace.Create());

        /// <summary>
        /// Initializes the next test iteration.
        /// </summary>
        /// <param name="iteration">The id of the next iteration.</param>
        /// <param name="logWriter">The log writer associated with the current test iteration.</param>
        /// <returns>True to start the specified test iteration, else false to stop exploring.</returns>
        internal bool InitializeNextIteration(uint iteration, LogWriter logWriter)
        {
            if (iteration > 0)
            {
                // Rotate the portfolio strategies using round-robin.
                var strategy = this.Portfolio.First.Value;
                this.Portfolio.RemoveFirst();
                this.Portfolio.AddLast(strategy);

                this.Graph.Clear();
                this.Trace.Clear();
            }

            // Release the operations of the previous iteration, which would otherwise be kept alive
            // by these buffers until the corresponding scheduling step of the next iteration.
            this.EnabledOperations.Clear();
            this.ReducedOperations.Clear();

            // Initialize any installed schedule reducers.
            foreach (var reducer in this.Reducers)
            {
                reducer.InitializeNextIteration(iteration);
            }

            this.Strategy.LogWriter = logWriter;
            return this.Strategy.InitializeNextIteration(iteration);
        }

        /// <summary>
        /// Returns the next controlled operation to schedule.
        /// </summary>
        /// <param name="ops">The set of available operations.</param>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="isYielding">True if the current operation is yielding, else false.</param>
        /// <param name="next">The next operation to schedule.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        /// <remarks>
        /// The set of schedulable operations is snapshotted into a reusable buffer once per
        /// scheduling step, rather than being passed down as a lazily evaluated query that each
        /// reducer and the strategy would re-enumerate several times over.
        /// <para>
        /// The snapshot is equivalent to the lazy query because no operation can change status
        /// while this method runs: the reducers only read <see cref="ControlledOperation.LastSchedulingPoint"/>
        /// and the last accessed shared state, the exploration strategies only read the operation
        /// identity, group and status, and neither writes back. This method also runs inside the
        /// runtime lock, so no other thread can intervene.
        /// </para>
        /// </remarks>
        internal bool GetNextOperation(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            bool isYielding, out ControlledOperation next)
        {
            // Filter out any operations that cannot be scheduled, preserving their relative order.
            var enabledOps = this.EnabledOperations;
            var reducedOps = this.ReducedOperations;
            enabledOps.Clear();
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                if (ops[idx].Status is OperationStatus.Enabled)
                {
                    enabledOps.Add(ops[idx]);
                }
            }

            if (enabledOps.Count > 0)
            {
                // Invoke any installed schedule reducers, swapping the two buffers so that the
                // output of each reducer becomes the input of the next one. A reducer that leaves
                // its output empty is reporting that no reduction applies, in which case the
                // operations from the previous stage are kept.
                for (int idx = 0; idx < this.Reducers.Count; ++idx)
                {
                    reducedOps.Clear();
                    this.Reducers[idx].ReduceOperations(enabledOps, current, reducedOps);
                    if (reducedOps.Count > 0)
                    {
                        var swap = enabledOps;
                        enabledOps = reducedOps;
                        reducedOps = swap;
                    }
                }

                // Invoke the strategy to choose the next operation.
                if (this.Strategy is InterleavingStrategy strategy &&
                    strategy.GetNextOperation(enabledOps, current, isYielding, out next))
                {
                    if (this.Configuration.IsTraceAnalysisEnabled)
                    {
                        this.Graph.Add(current);
                    }

                    this.Trace.AddSchedulingDecision(current, current.LastSchedulingPoint, next);
                    return true;
                }
            }

            next = null;
            return false;
        }

        /// <summary>
        /// Returns the next boolean choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="next">The next boolean choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal bool GetNextBoolean(ControlledOperation current, out bool next)
        {
            if (this.Strategy is InterleavingStrategy strategy &&
                strategy.GetNextBoolean(current, out next))
            {
                this.Trace.AddNondeterministicBooleanDecision(current, next);
                return true;
            }

            next = false;
            return false;
        }

        /// <summary>
        /// Returns the next integer choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next integer choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal bool GetNextInteger(ControlledOperation current, int maxValue, out int next)
        {
            if (this.Strategy is InterleavingStrategy strategy &&
                strategy.GetNextInteger(current, maxValue, out next))
            {
                this.Trace.AddNondeterministicIntegerDecision(current, next);
                return true;
            }

            next = 0;
            return false;
        }

        /// <summary>
        /// Returns the next delay.
        /// </summary>
        /// <param name="current">The operation requesting the delay.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next delay.</param>
        /// <returns>True if there is a next delay, else false.</returns>
        internal bool GetNextDelay(ControlledOperation current, int maxValue, out int next) =>
            (this.Strategy as FuzzingStrategy).GetNextDelay(current, maxValue, out next);

        /// <summary>
        /// Sets a checkpoint in the currently explored execution trace, that allows replaying all
        /// scheduling decisions until the checkpoint in subsequent iterations.
        /// </summary>
        internal ExecutionTrace CheckpointExecutionTrace() => this.PrefixTrace.ExtendOrReplace(this.Trace);

        /// <summary>
        /// Returns the name of the current exploration strategy.
        /// </summary>
        internal string GetStrategyName() => this.Strategy.GetName();

        /// <summary>
        /// Returns the number of strategies in the portfolio.
        /// </summary>
        internal int PortfolioSize => this.Portfolio.Count;

        /// <summary>
        /// Returns a description of the currently active strategy in the portfolio.
        /// </summary>
        internal string GetActiveStrategyDescription() => this.Strategy.GetDescription();

        /// <summary>
        /// Returns a description of the current exploration strategy in text format.
        /// </summary>
        internal string GetDescription() => this.Portfolio.Count > 1 ?
            this.SchedulingPolicy is SchedulingPolicy.Fuzzing ?
                $"portfolio[fuzzing,seed:{this.Strategy.RandomValueGenerator.Seed}]" :
                $"portfolio[{(this.Configuration.PortfolioMode.IsFair() ? "fair," : string.Empty)}seed:{this.Strategy.RandomValueGenerator.Seed}]" :
            this.Strategy.GetDescription();

        /// <summary>
        /// Returns the last scheduling error, or the empty string if there is none.
        /// </summary>
        internal string GetLastError() => this.Strategy.ErrorText;
    }
}
