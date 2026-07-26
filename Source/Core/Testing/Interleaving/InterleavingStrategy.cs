// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Testing.Interleaving
{
    /// <summary>
    /// Abstract exploration strategy used during controlled testing.
    /// </summary>
    internal abstract class InterleavingStrategy : Strategy
    {
        /// <summary>
        /// The execution prefix trace to try reproduce.
        /// </summary>
        internal ExecutionTrace TracePrefix;

        /// <summary>
        /// Initializes a new instance of the <see cref="InterleavingStrategy"/> class.
        /// </summary>
        protected InterleavingStrategy(Configuration configuration, bool isFair)
            : base(configuration, isFair)
        {
        }

        /// <inheritdoc/>
        internal override bool InitializeNextIteration(uint iteration)
        {
            this.StepCount = 0;
            return true;
        }

        /// <summary>
        /// Returns the next controlled operation to schedule.
        /// </summary>
        /// <param name="ops">Operations that can be scheduled.</param>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="isYielding">True if the current operation is yielding, else false.</param>
        /// <param name="next">The next operation to schedule.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal bool GetNextOperation(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            bool isYielding, out ControlledOperation next)
        {
            try
            {
                bool result = true;
                if (this.StepCount < this.TracePrefix.Length)
                {
                    ExecutionTrace.Step nextStep = this.TracePrefix[this.StepCount];
                    if (nextStep is ExecutionTrace.SchedulingStep step)
                    {
                        next = FindOperationWithId(ops, step.Value);
                        if (next is null)
                        {
                            this.ErrorText = this.FormatReplayError(nextStep.Index, $"cannot detect id '{step.Value}'");
                            throw new InvalidOperationException(this.ErrorText);
                        }
                        else if (step.SchedulingPoint != current.LastSchedulingPoint)
                        {
                            this.ErrorText = this.FormatReplayError(nextStep.Index,
                                $"expected scheduling point '{step.SchedulingPoint}' instead of '{current.LastSchedulingPoint}'");
                            throw new InvalidOperationException(this.ErrorText);
                        }
                    }
                    else
                    {
                        this.ErrorText = this.FormatReplayError(nextStep.Index, "next step is not a scheduling choice");
                        throw new InvalidOperationException(this.ErrorText);
                    }
                }
                else
                {
                    result = this.NextOperation(ops, current, isYielding, out next);
                }

                this.StepCount++;
                return result;
            }
            catch (InvalidOperationException ex)
            {
                this.LogWriter.LogError(ex.Message);
                next = null;
                return false;
            }
        }

        /// <summary>
        /// Returns the next controlled operation to schedule.
        /// </summary>
        /// <param name="ops">Operations that can be scheduled.</param>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="isYielding">True if the current operation is yielding, else false.</param>
        /// <param name="next">The next operation to schedule.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal abstract bool NextOperation(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            bool isYielding, out ControlledOperation next);

        /// <summary>
        /// Returns the next boolean choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="next">The next boolean choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal bool GetNextBoolean(ControlledOperation current, out bool next)
        {
            try
            {
                bool result = true;
                if (this.StepCount < this.TracePrefix.Length)
                {
                    ExecutionTrace.Step nextStep = this.TracePrefix[this.StepCount];
                    if (nextStep is ExecutionTrace.BooleanChoiceStep step)
                    {
                        next = step.Value;
                    }
                    else
                    {
                        this.ErrorText = this.FormatReplayError(nextStep.Index, "next step is not a nondeterministic choice");
                        throw new InvalidOperationException(this.ErrorText);
                    }
                }
                else
                {
                    result = this.NextBoolean(current, out next);
                }

                this.StepCount++;
                return result;
            }
            catch (InvalidOperationException ex)
            {
                this.LogWriter.LogError(ex.Message);
                next = false;
                return false;
            }
        }

        /// <summary>
        /// Returns the next boolean choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="next">The next boolean choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal abstract bool NextBoolean(ControlledOperation current, out bool next);

        /// <summary>
        /// Returns the next integer choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next integer choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal bool GetNextInteger(ControlledOperation current, int maxValue, out int next)
        {
            try
            {
                bool result = true;
                if (this.StepCount < this.TracePrefix.Length)
                {
                    ExecutionTrace.Step nextStep = this.TracePrefix[this.StepCount];
                    if (nextStep is ExecutionTrace.IntegerChoiceStep step)
                    {
                        next = step.Value;
                    }
                    else
                    {
                        this.ErrorText = this.FormatReplayError(nextStep.Index, "next step is not a nondeterministic choice");
                        throw new InvalidOperationException(this.ErrorText);
                    }
                }
                else
                {
                    result = this.NextInteger(current, maxValue, out next);
                }

                this.StepCount++;
                return result;
            }
            catch (InvalidOperationException ex)
            {
                this.LogWriter.LogError(ex.Message);
                next = 0;
                return false;
            }
        }

        /// <summary>
        /// Returns the next integer choice.
        /// </summary>
        /// <param name="current">The currently scheduled operation.</param>
        /// <param name="maxValue">The max value.</param>
        /// <param name="next">The next integer choice.</param>
        /// <returns>True if there is a next choice, else false.</returns>
        internal abstract bool NextInteger(ControlledOperation current, int maxValue, out int next);

        /// <summary>
        /// Returns the first operation in the specified list with the given id, or null if there
        /// is no such operation.
        /// </summary>
        /// <remarks>
        /// Returning null rather than throwing is load-bearing: a recorded id that is no longer
        /// schedulable is how both trace replay and depth-first search detect that exploration
        /// cannot continue.
        /// </remarks>
        protected static ControlledOperation FindOperationWithId(IReadOnlyList<ControlledOperation> ops, ulong id)
        {
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                if (ops[idx].Id == id)
                {
                    return ops[idx];
                }
            }

            return null;
        }

        /// <summary>
        /// Replaces the contents of <paramref name="presentGroups"/> with the operation groups
        /// represented among the specified operations.
        /// </summary>
        /// <remarks>
        /// The resulting count is how many distinct groups can be scheduled in this step, which is
        /// what the group-based strategies use to decide whether their group selection logic can
        /// make any difference.
        /// </remarks>
        protected static void CachePresentGroups(IReadOnlyList<ControlledOperation> ops,
            HashSet<OperationGroup> presentGroups)
        {
            presentGroups.Clear();
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                presentGroups.Add(ops[idx].Group);
            }
        }

        /// <summary>
        /// Returns the first group of <paramref name="orderedGroups"/> that is present among
        /// <paramref name="presentGroups"/>, or null if there is no such group.
        /// </summary>
        /// <remarks>
        /// The strategies keep their tracked groups in the order that expresses their policy, so
        /// the first match is the group that policy selects.
        /// </remarks>
        protected static OperationGroup FindFirstPresentGroup(List<OperationGroup> orderedGroups,
            HashSet<OperationGroup> presentGroups)
        {
            foreach (var group in orderedGroups)
            {
                if (presentGroups.Contains(group))
                {
                    return group;
                }
            }

            return null;
        }

        /// <summary>
        /// Replaces the contents of <paramref name="result"/> with the operations of
        /// <paramref name="ops"/> that are members of the specified group, preserving their order.
        /// </summary>
        protected static void SelectGroupMembers(IReadOnlyList<ControlledOperation> ops, OperationGroup group,
            List<ControlledOperation> result)
        {
            result.Clear();
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                if (group.IsMember(ops[idx]))
                {
                    result.Add(ops[idx]);
                }
            }
        }

        /// <summary>
        /// Resets the strategy.
        /// </summary>
        /// <remarks>
        /// This is typically invoked by parent strategies to reset child strategies.
        /// </remarks>
        internal virtual void Reset() => this.StepCount = 0;

        /// <summary>
        /// Formats the error message.
        /// </summary>
        private string FormatReplayError(int step, string reason)
        {
#if NET
            string[] traceTokens = new StackTrace().ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
#else
            string[] traceTokens = new StackTrace().ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
#endif
            string trace = string.Join(Environment.NewLine, traceTokens.Where(line => !line.Contains("at Microsoft.Coyote")));
            string info = this.Configuration.RandomGeneratorSeed.HasValue ?
                $" from execution with random seed '{this.Configuration.RandomGeneratorSeed}'" : string.Empty;
            return $"The trace{info} is not reproducible at execution step '{step}': {reason}." + Environment.NewLine + trace;
        }
    }
}
