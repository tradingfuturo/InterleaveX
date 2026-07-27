// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Testing.Interleaving
{
    /// <summary>
    /// A (fair) probabilistic priority-based exploration strategy.
    /// </summary>
    /// <remarks>
    /// This strategy is based on the PCT algorithm described in the following paper:
    /// https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/asplos277-pct.pdf.
    /// </remarks>
    internal sealed class PrioritizationStrategy : RandomStrategy
    {
        /// <summary>
        /// List of prioritized operation groups.
        /// </summary>
        private readonly List<OperationGroup> PrioritizedOperationGroups;

        /// <summary>
        /// Scheduling points in the current execution where a priority change should occur.
        /// </summary>
        private readonly HashSet<int> PriorityChangePoints;

        /// <summary>
        /// Reusable set of the operation groups represented among the operations that can be
        /// scheduled in the current scheduling step.
        /// </summary>
        private readonly HashSet<OperationGroup> PresentGroups;

        /// <summary>
        /// Reusable buffer holding the operations that belong to the highest priority group.
        /// </summary>
        private readonly List<ControlledOperation> HighestPriorityGroupOps;

        /// <summary>
        /// Max number of priority changes per iteration.
        /// </summary>
        private readonly int MaxPriorityChangesPerIteration;

        /// <summary>
        /// Max number of potential priority change points across all iterations.
        /// </summary>
        private int MaxPriorityChangePoints;

        /// <summary>
        /// Number of potential priority change points in the current iteration.
        /// </summary>
        private int NumPriorityChangePoints;

        /// <summary>
        /// Initializes a new instance of the <see cref="PrioritizationStrategy"/> class.
        /// </summary>
        internal PrioritizationStrategy(Configuration configuration, int maxPriorityChanges, bool isFair)
            : base(configuration, isFair)
        {
            this.PrioritizedOperationGroups = new List<OperationGroup>();
            this.PriorityChangePoints = new HashSet<int>();
            this.PresentGroups = new HashSet<OperationGroup>();
            this.HighestPriorityGroupOps = new List<ControlledOperation>();
            this.MaxPriorityChangesPerIteration = maxPriorityChanges;
            this.MaxPriorityChangePoints = 0;
            this.NumPriorityChangePoints = 0;
        }

        /// <inheritdoc/>
        internal override bool InitializeNextIteration(uint iteration)
        {
            if (this.NumPriorityChangePoints > 0)
            {
                this.PrioritizedOperationGroups.Clear();
                this.PriorityChangePoints.Clear();

                this.MaxPriorityChangePoints = Math.Max(this.MaxPriorityChangePoints, this.NumPriorityChangePoints);
                if (this.MaxPriorityChangesPerIteration > 0)
                {
                    var priorityChanges = this.RandomValueGenerator.Next(this.MaxPriorityChangesPerIteration) + 1;
                    var range = Enumerable.Range(0, this.MaxPriorityChangePoints);
                    foreach (int point in this.Shuffle(range).Take(priorityChanges))
                    {
                        this.PriorityChangePoints.Add(point);
                    }
                }

                this.DebugPrintPriorityChangePoints();
            }

            // Release the operations and groups of the previous iteration, which these buffers would
            // otherwise keep alive until the next scheduling step.
            this.PresentGroups.Clear();
            this.HighestPriorityGroupOps.Clear();

            this.NumPriorityChangePoints = 0;
            return base.InitializeNextIteration(iteration);
        }

        /// <inheritdoc/>
        internal override bool NextOperation(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            bool isYielding, out ControlledOperation next)
        {
            if (this.IsFair && this.StepCount >= this.Configuration.MaxUnfairSchedulingSteps)
            {
                return base.NextOperation(ops, current, isYielding, out next);
            }

            // Set the priority of any new operation groups.
            this.SetNewOperationGroupPriorities(ops, current);

            // Cache the groups represented among the schedulable operations. Neither the priority
            // change below nor anything else in this method modifies the operations or their group
            // membership, so this stays valid for both lookups that follow.
            CachePresentGroups(ops, this.PresentGroups);

            // Check if there are at least two operation groups that can be scheduled,
            // otherwise skip the priority checking and changing logic.
            if (this.PresentGroups.Count > 1)
            {
                // Change the priority of the highest priority operation group.
                this.PrioritizeNextOperationGroup();
                this.NumPriorityChangePoints++;

                // Get the operations that belong to the highest priority group.
                OperationGroup nextGroup = this.GetOperationGroupWithHighestPriority();
                SelectGroupMembers(ops, nextGroup, this.HighestPriorityGroupOps);
                ops = this.HighestPriorityGroupOps;
            }

            return base.NextOperation(ops, current, isYielding, out next);
        }

        /// <summary>
        /// Sets a random priority to any new operation groups.
        /// </summary>
        private void SetNewOperationGroupPriorities(IReadOnlyList<ControlledOperation> ops, ControlledOperation current)
        {
            this.PrioritizedOperationGroups.RemoveAll(group => group.IsCompleted());

            int count = this.PrioritizedOperationGroups.Count;
            if (count is 0)
            {
                this.PrioritizedOperationGroups.Add(current.Group);
            }

            // Randomize the priority of all new operation groups. The membership test is evaluated
            // for each operation in turn, so a group shared by several operations is only inserted
            // once, and the sequence of random values consumed here is unchanged.
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                var group = ops[idx].Group;
                if (this.PrioritizedOperationGroups.Contains(group))
                {
                    continue;
                }

                // Randomly choose a priority for this group.
                int index = this.RandomValueGenerator.Next(this.PrioritizedOperationGroups.Count + 1);
                this.PrioritizedOperationGroups.Insert(index, group);
                this.LogWriter.LogDebug("[coyote::strategy] Assigned priority '{0}' for operation group '{1}'.", index, group);
            }

            if (this.PrioritizedOperationGroups.Count > count)
            {
                this.DebugPrintPriorityList();
            }
        }

        /// <summary>
        /// Reduces the priority of highest priority operation group, if there is a priority change point
        /// installed on the current execution step.
        /// </summary>
        private void PrioritizeNextOperationGroup()
        {
            if (this.PriorityChangePoints.Contains(this.NumPriorityChangePoints))
            {
                // This scheduling step was chosen as a priority change point.
                OperationGroup group = this.GetOperationGroupWithHighestPriority();
                if (group != null)
                {
                    // Reduce the priority of the group by putting it in the end of the list.
                    this.PrioritizedOperationGroups.Remove(group);
                    this.PrioritizedOperationGroups.Add(group);
                    this.LogWriter.LogDebug("[coyote::strategy] Reduced the priority of operation group '{0}'.", group);
                }
            }
        }

        /// <summary>
        /// Returns the operation group with the highest priority that is represented among the
        /// operations cached in <see cref="PresentGroups"/>.
        /// </summary>
        private OperationGroup GetOperationGroupWithHighestPriority() =>
            FindFirstPresentGroup(this.PrioritizedOperationGroups, this.PresentGroups);

        /// <inheritdoc/>
        internal override string GetName() => (this.IsFair ? ExplorationStrategy.FairPrioritization : ExplorationStrategy.Prioritization).GetName();

        /// <inheritdoc/>
        internal override string GetDescription() =>
            $"{this.GetName()}[bound:{this.MaxPriorityChangesPerIteration},seed:{this.RandomValueGenerator.Seed}]";

        /// <summary>
        /// Shuffles the specified range using the Fisher-Yates algorithm.
        /// </summary>
        /// <remarks>
        /// See https://en.wikipedia.org/wiki/Fisher%E2%80%93Yates_shuffle.
        /// </remarks>
        private IList<int> Shuffle(IEnumerable<int> range)
        {
            var result = new List<int>(range);
            for (int idx = result.Count - 1; idx >= 1; idx--)
            {
                int point = this.RandomValueGenerator.Next(result.Count);
                int temp = result[idx];
                result[idx] = result[point];
                result[point] = temp;
            }

            return result;
        }

        /// <inheritdoc/>
        internal override void Reset()
        {
            this.NumPriorityChangePoints = 0;
            this.PrioritizedOperationGroups.Clear();
            this.PriorityChangePoints.Clear();
            base.Reset();
        }

        /// <summary>
        /// Print the operation group priority list, if debug is enabled.
        /// </summary>
        private void DebugPrintPriorityList()
        {
            this.LogWriter.LogDebug(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("[coyote::strategy] Updated operation group priority list: ");
                for (int idx = 0; idx < this.PrioritizedOperationGroups.Count; ++idx)
                {
                    var group = this.PrioritizedOperationGroups[idx];
                    if (group.Any(m => m.Status is OperationStatus.Enabled))
                    {
                        sb.AppendLine($"  |_ [{idx}] operation group with id '{group}' [enabled]");
                    }
                    else if (group.Any(m => m.Status != OperationStatus.Completed))
                    {
                        sb.AppendLine($"  |_ [{idx}] operation group with id '{group}'");
                    }
                }

                return sb.ToString();
            });
        }

        /// <summary>
        /// Print the priority change points, if debug is enabled.
        /// </summary>
        private void DebugPrintPriorityChangePoints()
        {
            this.LogWriter.LogDebug(() =>
            {
                var sb = new StringBuilder();
                if (this.PriorityChangePoints.Count > 0)
                {
                    // Sort them before printing for readability.
                    var sortedChangePoints = this.PriorityChangePoints.ToArray();
                    Array.Sort(sortedChangePoints);
                    var points = string.Join(", ", sortedChangePoints);
                    sb.AppendLine($"[coyote::strategy] Assigned '{sortedChangePoints.Length}' priority change points: {points}.");
                }
                else
                {
                    sb.AppendLine("[coyote::strategy] Assigned '0' priority change points.");
                }

                return sb.ToString();
            });
        }
    }
}
