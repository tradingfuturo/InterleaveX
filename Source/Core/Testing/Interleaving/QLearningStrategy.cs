// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using Microsoft.Coyote.Runtime;

namespace Microsoft.Coyote.Testing.Interleaving
{
    /// <summary>
    /// A probabilistic exploration strategy that uses Q-learning.
    /// </summary>
    /// <remarks>
    /// This strategy is described in the following paper:
    /// https://dl.acm.org/doi/10.1145/3428298.
    /// </remarks>
    internal sealed class QLearningStrategy : RandomStrategy
    {
        /// <summary>
        /// Map from program states to a map from next operations to their quality values.
        /// </summary>
        private readonly Dictionary<int, Dictionary<ulong, double>> OperationQTable;

        /// <summary>
        /// The path that is being executed during the current iteration. Each
        /// step of the execution is represented by an operation and a value
        /// representing the program state after the operation executed.
        /// </summary>
        /// <remarks>
        /// A list rather than a linked list, because one entry is appended per scheduling decision
        /// and per nondeterministic choice, and a linked list allocates a node for each of them. At
        /// the default fair step bound that is a hundred thousand nodes an iteration, all of it
        /// garbage once the iteration ends. Clearing a list keeps its capacity, so the storage is
        /// reused across iterations instead. Only sequential access is needed, by
        /// <see cref="LearnQValues"/>.
        /// </remarks>
        private readonly List<(ulong Op, SchedulingPointType Sp, int State)> ExecutionPath;

        /// <summary>
        /// Map from values representing program states to their transition
        /// frequency in the current execution path.
        /// </summary>
        private readonly Dictionary<int, ulong> TransitionFrequencies;

        /// <summary>
        /// The last chosen operation.
        /// </summary>
        private ulong LastOperation;

        /// <summary>
        /// Reusable map from the id of each operation that can be scheduled in the current
        /// scheduling step to that operation.
        /// </summary>
        private readonly Dictionary<ulong, ControlledOperation> SchedulableOpsById;

        /// <summary>
        /// Reusable buffer holding the operations that have a quality value in the current program
        /// state, in the order they are enumerated from the Q-table.
        /// </summary>
        private readonly List<ControlledOperation> CandidateOps;

        /// <summary>
        /// Reusable buffer holding the quality values that correspond to <see cref="CandidateOps"/>.
        /// </summary>
        private readonly List<double> CandidateQValues;

        /// <summary>
        /// Reusable buffer holding the quality values of a boolean or integer choice.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="CandidateQValues"/> because that one is paired with
        /// <see cref="CandidateOps"/>, and the two must stay the same length.
        /// </remarks>
        private readonly List<double> ChoiceQValues;

        /// <summary>
        /// The value of the learning rate.
        /// </summary>
        private readonly double LearningRate;

        /// <summary>
        /// The value of the discount factor.
        /// </summary>
        private readonly double Gamma;

        /// <summary>
        /// The op value denoting a true boolean choice.
        /// </summary>
        private readonly ulong TrueChoiceOpValue;

        /// <summary>
        /// The op value denoting a false boolean choice.
        /// </summary>
        private readonly ulong FalseChoiceOpValue;

        /// <summary>
        /// The op value denoting the min integer choice.
        /// </summary>
        private readonly ulong MinIntegerChoiceOpValue;

        /// <summary>
        /// The basic action reward.
        /// </summary>
        private readonly double BasicActionReward;

        /// <summary>
        /// The failure injection reward.
        /// </summary>
        private readonly double FailureInjectionReward;

        /// <summary>
        /// The number of explored executions.
        /// </summary>
        private int Epochs;

        /// <summary>
        /// Initializes a new instance of the <see cref="QLearningStrategy"/> class.
        /// It uses the specified random number generator.
        /// </summary>
        public QLearningStrategy(Configuration configuration)
            : base(configuration, false)
        {
            this.OperationQTable = new Dictionary<int, Dictionary<ulong, double>>();
            this.ExecutionPath = new List<(ulong, SchedulingPointType, int)>();
            this.TransitionFrequencies = new Dictionary<int, ulong>();
            this.SchedulableOpsById = new Dictionary<ulong, ControlledOperation>();
            this.CandidateOps = new List<ControlledOperation>();
            this.CandidateQValues = new List<double>();
            this.ChoiceQValues = new List<double>();
            this.LastOperation = 0;
            this.LearningRate = 0.3;
            this.Gamma = 0.7;
            this.TrueChoiceOpValue = ulong.MaxValue;
            this.FalseChoiceOpValue = ulong.MaxValue - 1;
            this.MinIntegerChoiceOpValue = ulong.MaxValue - 2;
            this.BasicActionReward = -1;
            this.FailureInjectionReward = -1000;
            this.Epochs = 0;
        }

        /// <summary>
        /// This strategy keys its Q-table on the program state, so it is the one strategy that
        /// needs the runtime to compute it.
        /// </summary>
        internal override bool RequiresImplicitProgramStateHashing => true;

        /// <inheritdoc/>
        internal override bool InitializeNextIteration(uint iteration)
        {
            this.LearnQValues();
            this.ExecutionPath.Clear();

            // Release the operations of the previous iteration, which these buffers would otherwise
            // keep alive until the next scheduling step that consults the Q-table.
            this.SchedulableOpsById.Clear();
            this.CandidateOps.Clear();

            this.LastOperation = 0;
            this.Epochs++;
            return base.InitializeNextIteration(iteration);
        }

        /// <inheritdoc/>
        internal override bool NextOperation(IReadOnlyList<ControlledOperation> ops, ControlledOperation current,
            bool isYielding, out ControlledOperation next)
        {
            int state = this.CaptureExecutionStep(current);
            this.InitializeOperationQValues(state, ops);

            next = this.GetNextOperationByPolicy(state, ops);
            this.LastOperation = next.Id;
            return true;
        }

        /// <inheritdoc/>
        internal override bool NextBoolean(ControlledOperation current, out bool next)
        {
            int state = this.CaptureExecutionStep(current);
            this.InitializeBooleanChoiceQValues(state);
            next = this.GetNextBooleanChoiceByPolicy(state);
            this.LastOperation = next ? this.TrueChoiceOpValue : this.FalseChoiceOpValue;
            return true;
        }

        /// <inheritdoc/>
        internal override bool NextInteger(ControlledOperation current, int maxValue, out int next)
        {
            int state = this.CaptureExecutionStep(current);
            this.InitializeIntegerChoiceQValues(state, maxValue);
            next = this.GetNextIntegerChoiceByPolicy(state, maxValue);
            this.LastOperation = this.MinIntegerChoiceOpValue - (ulong)next;
            return true;
        }

        /// <summary>
        /// Returns the next operation to schedule by drawing from the probability
        /// distribution over the specified state and enabled operations.
        /// </summary>
        private ControlledOperation GetNextOperationByPolicy(int state, IReadOnlyList<ControlledOperation> ops)
        {
            // Index the schedulable operations by id so that the membership test below is a lookup
            // rather than a scan of every operation per Q-table entry. Ids are unique among the
            // schedulable operations, because they key the map that these operations come from.
            this.SchedulableOpsById.Clear();
            for (int i = 0; i < ops.Count; ++i)
            {
                this.SchedulableOpsById[ops[i].Id] = ops[i];
            }

            // The Q-table is enumerated in its own order, and the index sampled from the resulting
            // distribution is resolved back through that same order, so it must not be reordered.
            var candidateOps = this.CandidateOps;
            var qValues = this.CandidateQValues;
            candidateOps.Clear();
            qValues.Clear();
            foreach (var pair in this.OperationQTable[state])
            {
                if (this.SchedulableOpsById.TryGetValue(pair.Key, out ControlledOperation op))
                {
                    candidateOps.Add(op);
                    qValues.Add(pair.Value);
                }
            }

            int idx = this.ChooseQValueIndexFromDistribution(qValues);
            return candidateOps[idx];
        }

        /// <summary>
        /// Returns the next boolean choice by drawing from the probability
        /// distribution over the specified state and boolean choices.
        /// </summary>
        private bool GetNextBooleanChoiceByPolicy(int state)
        {
            double trueQValue = this.OperationQTable[state][this.TrueChoiceOpValue];
            double falseQValue = this.OperationQTable[state][this.FalseChoiceOpValue];

            // Reuses the buffer rather than allocating one per choice, matching how the operation
            // path already works. The order the values are added in is what the sampled index is
            // resolved through, so it must stay true then false.
            var qValues = this.ChoiceQValues;
            qValues.Clear();
            qValues.Add(trueQValue);
            qValues.Add(falseQValue);

            int idx = this.ChooseQValueIndexFromDistribution(qValues);
            return idx == 0 ? true : false;
        }

        /// <summary>
        /// Returns the next integer choice by drawing from the probability
        /// distribution over the specified state and integer choices.
        /// </summary>
        private int GetNextIntegerChoiceByPolicy(int state, int maxValue)
        {
            var qValues = this.ChoiceQValues;
            qValues.Clear();
            for (ulong i = 0; i < (ulong)maxValue; ++i)
            {
                qValues.Add(this.OperationQTable[state][this.MinIntegerChoiceOpValue - i]);
            }

            return this.ChooseQValueIndexFromDistribution(qValues);
        }

        /// <summary>
        /// Returns an index of a Q value by drawing from the probability distribution
        /// over the specified Q values.
        /// </summary>
        private int ChooseQValueIndexFromDistribution(List<double> qValues)
        {
            double sum = 0;
            for (int i = 0; i < qValues.Count; ++i)
            {
                qValues[i] = Math.Exp(qValues[i]);
                sum += qValues[i];
            }

            for (int i = 0; i < qValues.Count; ++i)
            {
                qValues[i] /= sum;
            }

            // Change the shape of the distribution probability array to be cumulative. For example,
            // instead of [0.1, 0.2, 0.3, 0.4], we get [0.1, 0.3, 0.6, 1.0].
            //
            // Done in place, over the same buffer. This used to project through a LINQ 'Select' that
            // closed over a running total, which allocated a closure, a delegate, an iterator and a
            // list on every scheduling decision and every nondeterministic choice. The running total
            // is accumulated in the same order here, so each element gets the same sum of the same
            // terms and the sampled index is bit-for-bit identical; changing the order would perturb
            // the floating-point result and with it every schedule this strategy explores.
            double running = 0;
            for (int i = 0; i < qValues.Count; ++i)
            {
                double current = qValues[i];
                qValues[i] = current + running;
                running += current;
            }

            // Generate a random double value between 0.0 to 1.0.
            var rvalue = this.RandomValueGenerator.NextDouble();

            // Find the first index in the cumulative array that is greater
            // or equal than the generated random value.
            var idx = qValues.BinarySearch(rvalue);

            if (idx < 0)
            {
                // If an exact match is not found, List.BinarySearch will return the index
                // of the first items greater than the passed value, but in a specific form
                // (negative) we need to apply ~ to this negative value to get real index.
                idx = ~idx;
            }

            if (idx > qValues.Count - 1)
            {
                // Very rare case when probabilities do not sum to 1 because of
                // double precision issues (so sum is 0.999943 and so on).
                idx = qValues.Count - 1;
            }

            return idx;
        }

        /// <summary>
        /// Captures metadata related to the current execution step, and returns
        /// a value representing the current program state.
        /// </summary>
        private int CaptureExecutionStep(ControlledOperation current)
        {
            int state = current.LastHashedProgramState;

            // Update the execution path with the current state.
            this.ExecutionPath.Add((this.LastOperation, current.LastSchedulingPoint, state));

            // Increment the state transition frequency. A missing key reads as zero, so this is the
            // same as adding it first, in two lookups rather than three.
            this.TransitionFrequencies.TryGetValue(state, out ulong frequency);
            this.TransitionFrequencies[state] = frequency + 1;

            return state;
        }

        /// <summary>
        /// Initializes the Q values of all operations that can be chosen at the
        /// specified state that have not been previously encountered.
        /// </summary>
        private void InitializeOperationQValues(int state, IReadOnlyList<ControlledOperation> ops)
        {
            if (!this.OperationQTable.TryGetValue(state, out Dictionary<ulong, double> qValues))
            {
                qValues = new Dictionary<ulong, double>();
                this.OperationQTable.Add(state, qValues);
            }

            for (int idx = 0; idx < ops.Count; ++idx)
            {
                // Assign the same initial probability for all new operations.
                if (!qValues.ContainsKey(ops[idx].Id))
                {
                    qValues.Add(ops[idx].Id, 0);
                }
            }
        }

        /// <summary>
        /// Initializes the Q values of all boolean choices that can be chosen
        /// at the specified state that have not been previously encountered.
        /// </summary>
        private void InitializeBooleanChoiceQValues(int state)
        {
            if (!this.OperationQTable.TryGetValue(state, out Dictionary<ulong, double> qValues))
            {
                qValues = new Dictionary<ulong, double>();
                this.OperationQTable.Add(state, qValues);
            }

            if (!qValues.ContainsKey(this.TrueChoiceOpValue))
            {
                qValues.Add(this.TrueChoiceOpValue, 0);
            }

            if (!qValues.ContainsKey(this.FalseChoiceOpValue))
            {
                qValues.Add(this.FalseChoiceOpValue, 0);
            }
        }

        /// <summary>
        /// Initializes the Q values of all integer choices that can be chosen
        /// at the specified state that have not been previously encountered.
        /// </summary>
        private void InitializeIntegerChoiceQValues(int state, int maxValue)
        {
            if (!this.OperationQTable.TryGetValue(state, out Dictionary<ulong, double> qValues))
            {
                qValues = new Dictionary<ulong, double>();
                this.OperationQTable.Add(state, qValues);
            }

            for (ulong i = 0; i < (ulong)maxValue; ++i)
            {
                ulong opValue = this.MinIntegerChoiceOpValue - i;
                if (!qValues.ContainsKey(opValue))
                {
                    qValues.Add(opValue, 0);
                }
            }
        }

        /// <summary>
        /// Learn Q values using data from the current execution.
        /// </summary>
        private void LearnQValues()
        {
            for (int idx = 0; idx + 1 < this.ExecutionPath.Count; ++idx)
            {
                var (_, _, state) = this.ExecutionPath[idx];
                var (nextOp, nextSp, nextState) = this.ExecutionPath[idx + 1];

                // Compute the max Q value.
                double maxQ = double.MinValue;
                foreach (var nextOpQValuePair in this.OperationQTable[nextState])
                {
                    if (nextOpQValuePair.Value > maxQ)
                    {
                        maxQ = nextOpQValuePair.Value;
                    }
                }

                // Compute the reward. Program states that are visited with higher frequency result into lesser rewards.
                var freq = this.TransitionFrequencies[nextState];
                double reward = (nextSp == SchedulingPointType.InjectFailure ?
                    this.FailureInjectionReward : this.BasicActionReward) * freq;
                if (reward > 0)
                {
                    // The reward has underflowed.
                    reward = double.MinValue;
                }

                // Get the operations that are available from the current execution step.
                var currOpQValues = this.OperationQTable[state];
                if (!currOpQValues.ContainsKey(nextOp))
                {
                    currOpQValues.Add(nextOp, 0);
                }

                // Update the Q value of the next operation.
                // Q = [(1-a) * Q]  +  [a * (rt + (g * maxQ))]
                currOpQValues[nextOp] = ((1 - this.LearningRate) * currOpQValues[nextOp]) +
                    (this.LearningRate * (reward + (this.Gamma * maxQ)));
            }
        }

        /// <inheritdoc/>
        internal override string GetName() => ExplorationStrategy.QLearning.GetName();

        /// <inheritdoc/>
        internal override string GetDescription() => $"{this.GetName()}[seed:{this.RandomValueGenerator.Seed}]";
    }
}
