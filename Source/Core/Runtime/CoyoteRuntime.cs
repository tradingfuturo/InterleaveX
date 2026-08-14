// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Coverage;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime.CompilerServices;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.Testing;
using Microsoft.Coyote.Testing.Fuzzing;
using SpecMonitor = Microsoft.Coyote.Specifications.Monitor;

namespace Microsoft.Coyote.Runtime
{
    /// <summary>
    /// Runtime for controlling, scheduling and executing asynchronous operations.
    /// </summary>
    /// <remarks>
    /// Invoking scheduling methods is thread-safe.
    /// </remarks>
    internal sealed class CoyoteRuntime : ICoyoteRuntime, IDisposable
    {
        /// <summary>
        /// Provides access to the runtime associated with each controlled thread, or null
        /// if the current thread is not controlled.
        /// </summary>
        /// <remarks>
        /// In testing mode, each testing iteration uses a unique runtime instance. To safely
        /// retrieve it from static methods, we store it in each controlled thread local state.
        /// </remarks>
        [ThreadStatic]
        private static CoyoteRuntime ThreadLocalRuntime;

        /// <summary>
        /// Provides access to the runtime associated with each async local context, or null
        /// if the current async local context has no associated runtime.
        /// </summary>
        /// <remarks>
        /// In testing mode, each testing iteration uses a unique runtime instance. To safely
        /// retrieve it from static methods, we store it in each controlled async local state.
        /// </remarks>
        private static readonly AsyncLocal<CoyoteRuntime> AsyncLocalRuntime =
            new AsyncLocal<CoyoteRuntime>();

        /// <summary>
        /// The runtime installed in the current execution context.
        /// </summary>
        internal static CoyoteRuntime Current =>
            ThreadLocalRuntime ?? AsyncLocalRuntime.Value ?? RuntimeProvider.Default;

        /// <summary>
        /// Provides access to the operation executing on each controlled thread
        /// during systematic testing.
        /// </summary>
        [ThreadStatic]
        private static ControlledOperation ExecutingOperation;

        /// <summary>
        /// If true, the program execution is controlled by the runtime to
        /// explore interleavings and sources of nondeterminism, else false.
        /// </summary>
        internal static bool IsExecutionControlled => ExecutionControlledUseCount > 0;

        /// <summary>
        /// If true, collections are constructed as modelled instances, so that accesses to them can be
        /// observed, else false.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="IsExecutionControlled"/> because the two answer different questions.
        /// That one asks whether the runtime owns the schedule, which only interleaving does, and things
        /// that reshape execution to suit an explored schedule are right to ask it. This one asks only
        /// whether accesses are observed at all, which fuzzing does too, by perturbing them. Reading the
        /// interleaving-only answer here is what left every collection in a fuzzing run an ordinary
        /// instance: no race was reportable, and no delay was injected at any access.
        /// </remarks>
        internal static bool IsCollectionModellingEnabled => ModelledRuntimeUseCount > 0;

        /// <summary>
        /// If true, the currently executing thread is inside the synchronized section of the runtime.
        /// </summary>
        internal static bool IsExecutionSynchronized => SynchronizedSection.IsSynchronized();

        /// <summary>
        /// Count of controlled execution runtimes that have been used in this process.
        /// </summary>
        private static int ExecutionControlledUseCount;

        /// <summary>
        /// Count of runtimes that have been used in this process and observe execution under any policy.
        /// </summary>
        private static int ModelledRuntimeUseCount;

        /// <summary>
        /// The unique id of this runtime.
        /// </summary>
        internal readonly Guid Id;

        /// <summary>
        /// Counts the runtimes constructed in this process, so that each can be told apart from the
        /// ones before it by age as well as by identity.
        /// </summary>
        private static long GenerationCounter;

        /// <summary>
        /// How many runtimes had been constructed in this process when this one was, which orders this
        /// runtime against every other.
        /// </summary>
        /// <remarks>
        /// State shared across iterations is stamped with the generation that owns it, and only a
        /// strictly younger runtime may take it over. An identifier alone cannot express that: it says
        /// two runtimes differ, not which of them the state belongs to, so a thread left over from an
        /// iteration that has ended looks exactly like the arrival of a new one and takes ownership of
        /// state that a live iteration is in the middle of using.
        /// </remarks>
        internal readonly long Generation;

        /// <summary>
        /// The configuration used by the runtime.
        /// </summary>
        internal readonly Configuration Configuration;

        /// <summary>
        /// Scheduler that controls the execution of operations during testing.
        /// </summary>
        private readonly OperationScheduler Scheduler;

        /// <summary>
        /// The operation scheduling policy used by the runtime.
        /// </summary>
        internal SchedulingPolicy SchedulingPolicy => this.Scheduler?.SchedulingPolicy ??
            SchedulingPolicy.None;

        /// <summary>
        /// Responsible for scheduling controlled tasks.
        /// </summary>
        internal readonly ControlledTaskScheduler ControlledTaskScheduler;

        /// <summary>
        /// The synchronization context where controlled operations are executed.
        /// </summary>
        private readonly ControlledSynchronizationContext SyncContext;

        /// <summary>
        /// Creates tasks that are controlled and scheduled by the runtime.
        /// </summary>
        internal readonly TaskFactory TaskFactory;

        /// <summary>
        /// Pool of threads that execute controlled operations.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, Thread> ThreadPool;

        /// <summary>
        /// Map from unique operation ids to asynchronous operations.
        /// </summary>
        /// <remarks>
        /// This map retains every operation registered during the iteration, including the ones
        /// that already completed. Operations are looked up by id long after they complete, and a
        /// completed operation can be reset and reused, so entries are never removed from it. Use
        /// <see cref="SchedulableOperations"/> for the per-step scheduling work instead.
        /// </remarks>
        private readonly Dictionary<ulong, ControlledOperation> OperationMap;

        /// <summary>
        /// The operations that have not completed yet, in registration order.
        /// </summary>
        /// <remarks>
        /// This is the collection traversed on every scheduling step, so that the cost of a step
        /// scales with the number of live operations rather than with the number of operations the
        /// iteration has created so far.
        /// <para>
        /// Operations are added when they are registered and re-added if they are reset, both under
        /// the runtime lock. They are removed lazily by <see cref="TryEnableOperationsWithResolvedDependencies"/>,
        /// which already walks this collection on every scheduling step. Removal is lazy because a
        /// completed operation cannot be detected at the point it completes without synchronizing
        /// <see cref="ProcessUnhandledExceptionInOperation"/>, which runs on a thread that may be
        /// in the middle of being interrupted. This collection may therefore hold an operation that
        /// completed during the current step, which is harmless: every consumer selects operations
        /// by status, and a completed operation is neither enabled nor paused.
        /// </para>
        /// <para>
        /// The list is kept ordered by <see cref="ControlledOperation.RegistrationIndex"/> so that
        /// it presents operations in exactly the order <see cref="OperationMap"/> does, minus the
        /// completed ones. Preserving that order matters because it determines which operation a
        /// strategy's random draw selects.
        /// </para>
        /// </remarks>
        private readonly List<ControlledOperation> SchedulableOperations;

        /// <summary>
        /// Counter assigning each registered operation its position in the registration order.
        /// </summary>
        /// <remarks>
        /// Operation ids cannot be used for this: they are handed out by <see cref="GetNextOperationId"/>
        /// separately from registration, so an operation whose id was reserved up front through
        /// 'IActorRuntime.CreateActorId' or <see cref="Operation.GetNextId"/> can be registered long
        /// after an operation with a larger id.
        /// </remarks>
        private int OperationRegistrationCounter;

        /// <summary>
        /// Orders operations by <see cref="ControlledOperation.RegistrationIndex"/>, which is the
        /// order that <see cref="SchedulableOperations"/> is kept sorted by.
        /// </summary>
        private static readonly IComparer<ControlledOperation> RegistrationOrderComparer =
            Comparer<ControlledOperation>.Create((x, y) => x.RegistrationIndex.CompareTo(y.RegistrationIndex));

        /// <summary>
        /// How many scheduling steps apart the debug-only sweep of <see cref="OperationMap"/> in
        /// <see cref="AssertSchedulableOperationsInvariant"/> runs.
        /// </summary>
        private const int SchedulableOperationsAuditStride = 256;

        /// <summary>
        /// Map from newly created operations that have not started executing yet
        /// to an event handler that is set when the operation starts.
        /// </summary>
        private readonly Dictionary<ControlledOperation, ManualResetEventSlim> PendingStartOperationMap;

        /// <summary>
        /// Map from unique controlled thread names to their corresponding operations.
        /// </summary>
        private readonly ConcurrentDictionary<string, ControlledOperation> ControlledThreads;

        /// <summary>
        /// Map from controlled tasks to their corresponding operations.
        /// </summary>
        private readonly ConcurrentDictionary<Task, ControlledOperation> ControlledTasks;

        /// <summary>
        /// Map from known uncontrolled tasks to an optional string with debug information.
        /// </summary>
        private readonly ConcurrentDictionary<Task, string> UncontrolledTasks;

        /// <summary>
        /// Set of method calls with uncontrolled concurrency or other sources of nondeterminism.
        /// </summary>
        private readonly HashSet<string> UncontrolledInvocations;

        /// <summary>
        /// The currently scheduled operation during systematic testing.
        /// </summary>
        private ControlledOperation ScheduledOperation;

        /// <summary>
        /// The installed runtime extension, which by default is the <see cref="NullRuntimeExtension"/>.
        /// </summary>
        internal readonly IRuntimeExtension Extension;

        /// <summary>
        /// Data structure containing information regarding testing coverage.
        /// </summary>
        internal readonly CoverageInfo CoverageInfo;

        /// <summary>
        /// Responsible for generating random values.
        /// </summary>
        internal readonly IRandomValueGenerator ValueGenerator;

        /// <summary>
        /// Responsible for writing to the installed <see cref="ILogger"/>.
        /// </summary>
        internal readonly LogWriter LogWriter;

        /// <inheritdoc/>
        public ILogger Logger
        {
            get => this.LogWriter;
            set => this.LogWriter.SetLogger(value);
        }

        /// <summary>
        /// Manages all registered <see cref="IRuntimeLog"/> objects.
        /// </summary>
        internal readonly LogManager LogManager;

        /// <summary>
        /// List of all registered safety and liveness specification monitors.
        /// </summary>
        private readonly List<SpecMonitor> SpecificationMonitors;

        /// <summary>
        /// List of all registered task liveness monitors.
        /// </summary>
        private readonly List<TaskLivenessMonitor> TaskLivenessMonitors;

        /// <summary>
        /// List of all registered state hashing functions.
        /// </summary>
        private readonly List<Func<int>> StateHashingFunctions;

        /// <summary>
        /// The runtime completion source.
        /// </summary>
        private readonly TaskCompletionSource<bool> CompletionSource;

        /// <summary>
        /// Object that is used to synchronize access to the runtime.
        /// </summary>
        private readonly object RuntimeLock;

        /// <summary>
        /// Produces tokens for canceling asynchronous operations when the runtime detaches.
        /// </summary>
        private readonly CancellationTokenSource CancellationSource;

        /// <summary>
        /// Monotonically increasing operation id counter.
        /// </summary>
        private long OperationIdCounter;

        /// <summary>
        /// Records if the runtime is running.
        /// </summary>
        internal volatile bool IsRunning;

        /// <summary>
        /// The execution status of the runtime.
        /// </summary>
        /// <remarks>
        /// Volatile because the rewritten synchronization shims read it outside the runtime lock, on
        /// threads that are unwinding after their iteration ended, to decide whether they may still touch
        /// shared synchronization state.
        /// </remarks>
        private volatile ExecutionStatus ExecutionStatusValue;

        /// <summary>
        /// The execution status of the runtime.
        /// </summary>
        internal ExecutionStatus ExecutionStatus
        {
            get => this.ExecutionStatusValue;
            private set => this.ExecutionStatusValue = value;
        }

        /// <summary>
        /// True if this runtime has stopped executing the current test iteration, which happens once
        /// it detaches.
        /// </summary>
        /// <remarks>
        /// Reading this without holding the runtime lock is safe, because the status only ever moves
        /// away from <see cref="ExecutionStatus.Running"/> and never back. The rewritten
        /// synchronization shims read it to recognize operations that are still unwinding after their
        /// iteration was torn down: such operations must not touch shared synchronization state,
        /// because the engine clears it concurrently and repopulates it for the next iteration.
        /// </remarks>
        internal bool HasExecutionEnded => this.ExecutionStatus != ExecutionStatus.Running;

        /// <summary>
        /// If this value is not null, then it represents the last scheduling point that
        /// was postponed, which the runtime will try to schedule in the next available
        /// thread that invokes a scheduling point.
        /// </summary>
        /// <remarks>
        /// A scheduling point can be postponed in two scenarios. The first scenario is
        /// when an uncontrolled thread creates a new controlled operation and tries to
        /// schedule it, but this is only allowed from a controlled thread. In this case,
        /// the runtime will resume scheduling from the next available controlled thread.
        /// The second scenario is when a controlled operation waits or completes, but a
        /// potential deadlock is found due to uncontrolled concurrency that has not been
        /// resolved yet. In this case, the runtime will resume scheduling from the next
        /// available uncontrolled thread, unless there is a genuine deadlock.
        /// </remarks>
        private SchedulingPointType? LastPostponedSchedulingPoint;

        /// <summary>
        /// Value that suppresses interleavings of enabled operations when it is non-zero.
        /// </summary>
        private uint ScheduleSuppressionCount;

        /// <summary>
        /// True if the runtime is currently executing inside a specification, else false.
        /// </summary>
        private bool IsSpecificationInvoked;

        /// <summary>
        /// True if uncontrolled concurrency was detected, else false.
        /// </summary>
        private bool IsUncontrolledConcurrencyDetected;

        /// <summary>
        /// Associated with the bug report is an optional unhandled exception.
        /// </summary>
        private Exception UnhandledException;

        /// <summary>
        /// The max number of operations that were enabled at the same time.
        /// </summary>
        private uint MaxConcurrencyDegree;

        /// <summary>
        /// Bug report.
        /// </summary>
        internal string BugReport { get; private set; }

        /// <inheritdoc/>
        public event OnFailureHandler OnFailure;

        /// <summary>
        /// Initializes a new instance of the <see cref="CoyoteRuntime"/> class.
        /// </summary>
        internal static CoyoteRuntime Create(Configuration configuration, IRandomValueGenerator valueGenerator,
            LogWriter logWriter, LogManager logManager, IRuntimeExtension extension) =>
            new CoyoteRuntime(configuration, null, valueGenerator, logWriter, logManager, extension);

        /// <summary>
        /// Initializes a new instance of the <see cref="CoyoteRuntime"/> class.
        /// </summary>
        internal static CoyoteRuntime Create(Configuration configuration, OperationScheduler scheduler,
            LogWriter logWriter, LogManager logManager, IRuntimeExtension extension) =>
            new CoyoteRuntime(configuration, scheduler, scheduler.ValueGenerator, logWriter, logManager, extension);

        /// <summary>
        /// Initializes a new instance of the <see cref="CoyoteRuntime"/> class.
        /// </summary>
        private CoyoteRuntime(Configuration configuration, OperationScheduler scheduler, IRandomValueGenerator valueGenerator,
            LogWriter logWriter, LogManager logManager, IRuntimeExtension extension)
        {
            // Registers the runtime with the provider which in return assigns a unique identifier.
            this.Id = RuntimeProvider.Register(this);
            this.Generation = Interlocked.Increment(ref GenerationCounter);

            this.Configuration = configuration;
            this.Scheduler = scheduler;
            this.RuntimeLock = new object();
            this.CancellationSource = new CancellationTokenSource();
            this.OperationIdCounter = 0;
            this.IsRunning = true;
            this.ExecutionStatus = ExecutionStatus.Running;
            this.ScheduleSuppressionCount = 0;
            this.IsSpecificationInvoked = false;
            this.IsUncontrolledConcurrencyDetected = false;
            this.LastPostponedSchedulingPoint = null;
            this.MaxConcurrencyDegree = 0;

            this.ThreadPool = new ConcurrentDictionary<ulong, Thread>();
            this.OperationMap = new Dictionary<ulong, ControlledOperation>();
            this.SchedulableOperations = new List<ControlledOperation>();
            this.OperationRegistrationCounter = 0;
            this.PendingStartOperationMap = new Dictionary<ControlledOperation, ManualResetEventSlim>();
            this.ControlledThreads = new ConcurrentDictionary<string, ControlledOperation>();
            this.ControlledTasks = new ConcurrentDictionary<Task, ControlledOperation>();
            this.UncontrolledTasks = new ConcurrentDictionary<Task, string>();
            this.UncontrolledInvocations = new HashSet<string>();
            this.CompletionSource = new TaskCompletionSource<bool>();

            if (this.SchedulingPolicy != SchedulingPolicy.None)
            {
                Interlocked.Increment(ref ModelledRuntimeUseCount);
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    Interlocked.Increment(ref ExecutionControlledUseCount);
                }
            }

            this.Extension = extension ?? NullRuntimeExtension.Instance;
            this.CoverageInfo = this.Extension.GetCoverageInfo() ?? new CoverageInfo();
            this.ValueGenerator = valueGenerator;
            this.LogWriter = logWriter;
            this.LogManager = logManager;
            this.SpecificationMonitors = new List<SpecMonitor>();
            this.TaskLivenessMonitors = new List<TaskLivenessMonitor>();
            this.StateHashingFunctions = new List<Func<int>>();

            this.ControlledTaskScheduler = new ControlledTaskScheduler(this);
            this.SyncContext = new ControlledSynchronizationContext(this);
            this.TaskFactory = new TaskFactory(CancellationToken.None, TaskCreationOptions.HideScheduler,
                TaskContinuationOptions.HideScheduler, this.ControlledTaskScheduler);
        }

        /// <summary>
        /// Runs the specified test method.
        /// </summary>
        internal Task RunTestAsync(Delegate testMethod, string testName)
        {
            this.LogWriter.LogInfo("[coyote::test] Runtime '{0}' started {1} on thread '{2}' using the '{3}' strategy.",
                this.Id, string.IsNullOrEmpty(testName) ? "the test" : $"'{testName}'",
                Thread.CurrentThread.ManagedThreadId, this.Scheduler.GetStrategyName());
            this.Assert(testMethod != null, "Unable to execute a null test method.");

            ControlledOperation op = this.CreateControlledOperation();
            Action runTest = () =>
            {
                Task task = Task.CompletedTask;
                if (testMethod is Action<ICoyoteRuntime> actionWithRuntime)
                {
                    actionWithRuntime(this);
                }
                else if (testMethod is Func<ICoyoteRuntime, Task> functionWithRuntime)
                {
                    task = functionWithRuntime(this);
                }
                else if (this.Extension.RunTest(testMethod, out Task extensionTask))
                {
                    task = extensionTask;
                }
                else if (testMethod is Action action)
                {
                    action();
                }
                else if (testMethod is Func<Task> function)
                {
                    task = function();
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported test delegate of type '{testMethod.GetType()}'.");
                }

                // Wait for the task to complete and propagate any exceptions.
                this.RegisterKnownControlledTask(task);
                TaskServices.WaitUntilTaskCompletes(this, op, task);
                task.GetAwaiter().GetResult();

                // Wait for any operations managed by the runtime extension to reach quiescence and propagate any exceptions.
                // This is required in tests that use a runtime extension so that the test does not terminate early, because
                // the main thread can complete without waiting for the extended operations to reach quiescence.
                Task extensionQuiescenceTask = this.Extension.WaitUntilQuiescenceAsync();
                this.RegisterKnownControlledTask(extensionQuiescenceTask);
                TaskServices.WaitUntilTaskCompletes(this, op, extensionQuiescenceTask);
                extensionQuiescenceTask.GetAwaiter().GetResult();
            };

            // The thread running the test method is never observed by the program under test, so it can
            // be reused. Note that this operation detaches the runtime in its post-condition, so this
            // thread always retires rather than returning to the pool. It still benefits from reusing a
            // thread that an earlier iteration left parked.
            this.RunOnControlledThread(op, runTest, postCondition: () =>
            {
                using (SynchronizedSection.Enter(this.RuntimeLock))
                {
                    // Checks for any liveness errors at test termination.
                    this.CheckLivenessErrors();
                    this.Detach(ExecutionStatus.PathExplored);
                }
            });

            // Start running a background monitor that checks for potential deadlocks. This
            // mechanism is defensive for cases where there is uncontrolled concurrency or
            // synchronization primitives, and happens in conjunction with the deterministic
            // deadlock detection mechanism when scheduling controlled operations.
            this.StartMonitoringDeadlocks();
            return this.CompletionSource.Task;
        }

        /// <summary>
        /// Schedules the specified task to execute on the controlled thread pool.
        /// </summary>
        internal void Schedule(Task task)
        {
            // Check if an existing controlled operation is stored in the state of the task.
            ControlledOperation op;
            if (task.AsyncState is ControlledOperation existingOp)
            {
                op = existingOp;
                this.TryResetOperation(op);
            }
            else
            {
                op = this.CreateControlledOperation();
            }

            // Register this task as a known controlled task.
            this.ControlledTasks.TryAdd(task, op);

            Action runTask = () => this.ControlledTaskScheduler.ExecuteTask(task);

            // The thread executing this task is never observed by the program under test, so it can be
            // reused once the task completes.
            this.RunOnControlledThread(op, runTask);

            // Add a scheduling point to explore interleavings between the current operation
            // and the operation that was just scheduled.
            this.ScheduleNextOperation(default, SchedulingPointType.Create);
        }

        /// <summary>
        /// Schedules the specified continuation to execute on the controlled thread pool.
        /// </summary>
        internal void Schedule(Action continuation, OperationGroup group = null, Action preCondition = null, Action postCondition = null)
        {
            ControlledOperation op = this.CreateControlledOperation(group: group ?? ExecutingOperation?.Group);

            // The thread executing this continuation is never observed by the program under test, so it
            // can be reused once the continuation completes.
            this.RunOnControlledThread(op, continuation, preCondition, postCondition);

            // Add a scheduling point to explore interleavings between the current operation
            // and the operation that was just scheduled.
            this.ScheduleNextOperation(default, SchedulingPointType.ContinueWith);
        }

        /// <summary>
        /// Registers a mapping from the specified continuation action to its awaiting operation's group.
        /// When the continuation is later posted through <see cref="ControlledSynchronizationContext.Post"/>,
        /// the group is looked up via <see cref="TryGetContinuationGroup"/> and passed to
        /// <see cref="Schedule(Action, OperationGroup, Action, Action)"/>, preserving the awaiting
        /// operation's group instead of using the completing operation's group.
        /// </summary>
        internal void RegisterContinuationGroup(Action continuation, OperationGroup group)
        {
            if (continuation != null && group != null)
            {
                this.ContinuationGroups[continuation] = group;
            }
        }

        /// <summary>
        /// Returns a SynchronizationContext wrapper with a different object identity than the
        /// main <see cref="ControlledSynchronizationContext"/>. This prevents the .NET runtime
        /// from inlining task continuations, forcing them through
        /// <see cref="SynchronizationContext.Post"/> instead.
        /// </summary>
        internal SynchronizationContext GetAntiInlineSyncContext() =>
            this.SyncContext.GetAntiInlineContext();

        /// <summary>
        /// Prepares the specified continuation to resume under the control of this runtime, installing the
        /// anti-inlining synchronization context and yielding the context to restore once the continuation
        /// has been handed to the underlying awaiter. Returns false if the continuation must be dropped.
        /// </summary>
        /// <remarks>
        /// Returns false when the continuation belongs to an async operation whose controlling runtime is
        /// gone (or is being torn down) — for example a long-lived background loop that leaked across
        /// testing iterations and is now resuming against a disposed runtime. Such a continuation can no
        /// longer be controlled, so the caller must drop it rather than let the failure crash the test
        /// host. A live, controlled continuation never fails here: this setup only faults once the owning
        /// runtime's state has been disposed.
        /// <para>
        /// The failure is swallowed deliberately and broadly. Returning the executing thread cleanly to
        /// the scheduler keeps the runtime consistent; letting the exception propagate instead (it
        /// surfaces as a <see cref="ThreadInterruptedException"/> when the runtime interrupts threads
        /// during teardown) leaves the awaiting operation half-registered and either deadlocks the run or
        /// escapes to the thread pool as an unhandled exception that aborts the whole test run.
        /// </para>
        /// </remarks>
        internal bool TryPrepareContinuation(Action continuation, out SynchronizationContext savedSyncContext)
        {
            savedSyncContext = SynchronizationContext.Current;
            try
            {
                OperationGroup group = this.GetExecutingOperationUnsafe()?.Group;
                this.RegisterContinuationGroup(continuation, group);
                SynchronizationContext.SetSynchronizationContext(this.GetAntiInlineSyncContext());
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the specified failure, raised while handing a prepared continuation to the
        /// underlying awaiter, means the continuation is orphaned and must be dropped.
        /// </summary>
        /// <remarks>
        /// Handing the continuation over can itself post it through <see cref="ControlledSynchronizationContext"/>,
        /// which schedules against this runtime and therefore fails in the same teardown window that
        /// <see cref="TryPrepareContinuation"/> guards against. Only that window is swallowed: this runtime
        /// has already stopped running, or the failure is the <see cref="ThreadInterruptedException"/> that
        /// teardown raises on the threads it interrupts. Any other failure is a genuine registration error
        /// and must surface, because dropping it leaves the awaiting operation paused forever and the run
        /// reports a deadlock that has nothing to do with the program under test.
        /// </remarks>
        internal bool IsContinuationOrphaned(Exception exception) =>
            exception is ThreadInterruptedException || this.ExecutionStatus != ExecutionStatus.Running;

        /// <summary>
        /// A dictionary that maps continuation actions to their awaiting operation's group.
        /// Used by <see cref="ControlledSynchronizationContext.Post"/> to preserve the group
        /// when a continuation is posted through the synchronization context.
        /// </summary>
        private readonly ConcurrentDictionary<Action, OperationGroup> ContinuationGroups =
            new ConcurrentDictionary<Action, OperationGroup>();

        /// <summary>
        /// Tries to retrieve and remove the continuation group registered for the given
        /// continuation action. Returns null if no group was registered.
        /// </summary>
        internal OperationGroup TryGetContinuationGroup(object state)
        {
            if (state is Action action && this.ContinuationGroups.TryRemove(action, out OperationGroup group))
            {
                return group;
            }

            return null;
        }

        /// <summary>
        /// Schedules the specified delay to be executed asynchronously.
        /// </summary>
        internal Task ScheduleDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (delay.TotalMilliseconds is 0)
            {
                // If the delay is 0, then complete synchronously.
                return Task.CompletedTask;
            }

            if (delay == Timeout.InfiniteTimeSpan)
            {
                // Infinite is a contract, not a large value for a strategy to fuzz. Only the token
                // can complete this delay, matching the behavior outside systematic execution.
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                uint timeout = (uint)this.GetNextNondeterministicIntegerChoice((int)this.Configuration.TimeoutDelay, null, null);
                if (timeout is 0)
                {
                    // If the delay is 0, then complete synchronously.
                    return Task.CompletedTask;
                }

                // TODO: cache the dummy delay action to optimize memory.
                // TODO: figure out a good strategy for grouping delays, especially if they
                // are shared in different contexts and not awaited immediately.
                ControlledOperation op = this.CreateControlledOperation(group: ExecutingOperation?.Group);
                return this.TaskFactory.StartNew(state =>
                {
                    var delayedOp = state as ControlledOperation;
                    delayedOp.PauseWithDelay(timeout, cancellationToken);
                    this.ScheduleNextOperation(delayedOp, SchedulingPointType.Yield);
                    cancellationToken.ThrowIfCancellationRequested();
                },
                op,
                cancellationToken,
                this.TaskFactory.CreationOptions | TaskCreationOptions.DenyChildAttach,
                this.TaskFactory.Scheduler);
            }

            if (!this.TryGetExecutingOperation(out ControlledOperation current))
            {
                // Cannot fuzz the delay of an uncontrolled operation.
                return Task.Delay(delay, cancellationToken);
            }

            // TODO: we need to come up with something better!
            // Fuzz the delay.
            double boundedDelay = Math.Min(
                delay.TotalMilliseconds, this.Configuration.MaxFuzzingDelay);
            int maxDelay = boundedDelay >= int.MaxValue ? int.MaxValue : (int)boundedDelay;
            return Task.Delay(TimeSpan.FromMilliseconds(
                this.GetNondeterministicDelay(current, maxDelay)), cancellationToken);
        }

        /// <summary>
        /// Creates a new controlled thread for executing the specified operation. The operation executes
        /// the given logic alongside an optional pre-condition and post-condition. The controlled thread
        /// optionally uses the specified max stack size.
        /// </summary>
        internal Thread CreateControlledThread(ControlledOperation op, Delegate logic, Action preCondition = null,
            Action postCondition = null, int maxStackSize = 0)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus != ExecutionStatus.Running)
                {
                    throw new ThreadInterruptedException();
                }

                // Create a new thread that is instrumented to control and execute the operation.
                var thread = new Thread(input => this.ExecuteOperation(op, logic, input, preCondition, postCondition),
                    maxStackSize);

                thread.Name = Guid.NewGuid().ToString();
                thread.IsBackground = true;

                this.PublishThreadMappings(op, thread);
                return thread;
            }
        }

        /// <summary>
        /// Associates the specified operation with the specified thread in both directions.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller holds the runtime lock.
        /// </remarks>
        private void PublishThreadMappings(ControlledOperation op, Thread thread)
        {
            this.ThreadPool.AddOrUpdate(op.Id, thread, (id, oldThread) => thread);
            this.ControlledThreads.AddOrUpdate(thread.Name, op, (threadName, oldOp) => op);
        }

        /// <summary>
        /// Removes the association between the specified operation and the specified thread.
        /// </summary>
        /// <remarks>
        /// It is assumed that the caller holds the runtime lock. The operation is unmapped only if this
        /// thread is still the one associated with it: an operation can be reset and reused, and
        /// operation ids are reused, so a newer thread may already own this id and must stay reachable
        /// for <see cref="Detach"/> to interrupt it. Matching on the value is what expresses that.
        /// </remarks>
        private void RemoveThreadMappings(ControlledOperation op, Thread thread)
        {
            this.ControlledThreads.TryRemove(thread.Name, out ControlledOperation _);
            (this.ThreadPool as ICollection<KeyValuePair<ulong, Thread>>).Remove(
                new KeyValuePair<ulong, Thread>(op.Id, thread));
        }

        /// <summary>
        /// Executes the specified operation on the current thread, alongside an optional pre-condition
        /// and post-condition.
        /// </summary>
        /// <remarks>
        /// This always completes the operation, either normally or through
        /// <see cref="ProcessUnhandledExceptionInOperation"/>, which is what allows <see cref="Detach"/>
        /// to distinguish an operation that is still running from one that has finished.
        /// </remarks>
        private void ExecuteOperation(ControlledOperation op, Delegate logic, object input,
            Action preCondition, Action postCondition)
        {
            try
            {
                // Start executing the operation.
                this.OnStarted(op);

                // If fuzzing is enabled, and this is not the first started operation,
                // then try to delay it to explore race conditions.
                if (this.SchedulingPolicy is SchedulingPolicy.Fuzzing && op.Id > 0)
                {
                    this.DelayOperation(op);
                }

                // Execute the optional pre-condition.
                preCondition?.Invoke();

                // Execute the controlled logic.
                if (logic is ThreadStart threadStart)
                {
                    threadStart();
                }
                else if (logic is ParameterizedThreadStart parameterizedThreadStart)
                {
                    parameterizedThreadStart(input);
                }
                else if (logic is Action action)
                {
                    action();
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported controlled logic of type '{logic.GetType()}'.");
                }

                // Complete the operation and schedule the next enabled operation.
                this.OnCompleted(op);

                // Execute the optional post-condition.
                postCondition?.Invoke();

                // Schedule the next operation, if there is one enabled.
                this.ScheduleNextOperation(op, SchedulingPointType.Complete);
            }
            catch (Exception ex)
            {
                this.ProcessUnhandledExceptionInOperation(op, ex);
            }
            finally
            {
                CleanCurrentExecutionContext();
            }
        }

        /// <summary>
        /// Executes the specified operation on a pooled controlled thread, alongside an optional
        /// pre-condition and post-condition.
        /// </summary>
        /// <remarks>
        /// This is the counterpart of <see cref="CreateControlledThread"/> for operations whose
        /// <see cref="Thread"/> object is never handed to the program under test, so nothing can join it
        /// or assert on its state. Such a thread can be reused once the operation completes, which avoids
        /// creating one thread per task and per continuation in every testing iteration. Use
        /// <see cref="CreateControlledThread"/> instead whenever the thread object is handed out, because
        /// user code then relies on it terminating with its operation.
        ///
        /// Reuse is still indirectly observable: user code can read
        /// <see cref="Thread.CurrentThread"/> identity, and thread-static state it writes survives onto
        /// whichever operation this thread executes next. The runtime clears its own per-thread state
        /// between operations, but application-owned thread-static state is not cleared, which is an
        /// inherent limitation of any thread reuse.
        /// </remarks>
        internal void RunOnControlledThread(ControlledOperation op, Delegate logic, Action preCondition = null,
            Action postCondition = null)
        {
            if (!this.Configuration.IsControlledThreadPoolingEnabled)
            {
                Thread unpooled = this.CreateControlledThread(op, logic, preCondition, postCondition);
                this.StartControlledThread(unpooled, op: op);
                return;
            }

            PooledThread worker;
            ControlledWorkItem workItem;

            // Capture the caller's execution context, so that the operation observes the same ambient
            // state it would observe on a dedicated thread, whose Start captures the creator's context.
            // This is what carries async-local state installed outside of controlled code into the
            // operation, such as a test harness registering service overrides before running the test.
            // The worker runs the operation under this captured context, and discards any changes the
            // operation made to it once the operation completes.
            ExecutionContext creatorContext = ExecutionContext.Capture();

            // These are two separate synchronized sections rather than one, mirroring the pairing of
            // CreateControlledThread and StartControlledThread on the unpooled path. Merging them would
            // remove a window in which the runtime lock is released, changing which interleavings are
            // reachable and therefore what the exploration strategies explore.
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus != ExecutionStatus.Running)
                {
                    throw new ThreadInterruptedException();
                }

                worker = ControlledThreadPool.Instance.Rent();
                workItem = new ControlledWorkItem(this, op, logic, preCondition, postCondition,
                    creatorContext, worker);
                this.PublishThreadMappings(op, worker.OSThread);
            }

            // A reserved thread is not reachable from the pool, so nothing else can ever wake it. If this
            // method leaves without handing it an operation or releasing it, that thread stays blocked for
            // the life of the process, and the mappings published above outlive the operation that never
            // started. This happens when the runtime detaches in between the two sections and the
            // interrupt it sends lands on the section below.
            bool isWorkerOwned = false;
            try
            {
                ControlledThreadPool.ReservationFaultInjector?.Invoke(worker);
                using (SynchronizedSection.Enter(this.RuntimeLock))
                {
                    if (this.ExecutionStatus is ExecutionStatus.Running)
                    {
                        this.StartOperation(op);
                        worker.Dispatch(workItem.Run);
                        isWorkerOwned = true;
                    }

                    // Otherwise the runtime detached between the two sections, so the operation must
                    // not start. Leaving the worker unowned rolls the reservation back through the
                    // finally below, which is the same path a failure in this section takes: both must
                    // release the thread and remove the mappings, and having one path rather than two
                    // is what keeps them from diverging.
                }
            }
            finally
            {
                if (!isWorkerOwned)
                {
                    this.AbandonControlledThread(op, worker);
                }
            }
        }

        /// <summary>
        /// One execution of a controlled operation on a pooled thread.
        /// </summary>
        /// <remarks>
        /// The state of a dispatch is held in fields rather than captured in a closure because a pooled
        /// thread outlives the operation it runs: whatever the work item references stays reachable
        /// until that thread is handed its next operation, so capturing the enclosing scope would pin
        /// every local in it, and the runtime, for that whole time. Naming the state also lets the
        /// callback below be allocated once instead of once per execution.
        /// </remarks>
        private sealed class ControlledWorkItem
        {
            /// <summary>
            /// Runs a work item passed as the state of an <see cref="ExecutionContext"/>.
            /// </summary>
            private static readonly ContextCallback ExecuteCallback =
                state => (state as ControlledWorkItem).Execute();

            private readonly CoyoteRuntime Runtime;
            private readonly ControlledOperation Operation;
            private readonly Delegate Logic;
            private readonly Action PreCondition;
            private readonly Action PostCondition;
            private readonly PooledThread Worker;

            /// <summary>
            /// The execution context captured from the creator of this operation, or null if the
            /// creator suppressed its flow.
            /// </summary>
            private readonly ExecutionContext CreatorContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="ControlledWorkItem"/> class.
            /// </summary>
            internal ControlledWorkItem(CoyoteRuntime runtime, ControlledOperation op, Delegate logic,
                Action preCondition, Action postCondition, ExecutionContext creatorContext, PooledThread worker)
            {
                this.Runtime = runtime;
                this.Operation = op;
                this.Logic = logic;
                this.PreCondition = preCondition;
                this.PostCondition = postCondition;
                this.CreatorContext = creatorContext;
                this.Worker = worker;
            }

            /// <summary>
            /// Executes this operation and releases the thread, returning what the thread must do next.
            /// </summary>
            internal WorkerDisposition Run()
            {
                if (this.CreatorContext is null)
                {
                    // The creator suppressed execution context flow, which is also what a dedicated
                    // thread would have started under.
                    this.Execute();
                }
                else
                {
                    ExecutionContext.Run(this.CreatorContext, ExecuteCallback, this);
                }

                // Deliberately outside the exception handling in ExecuteOperation. If releasing the
                // thread fails, which happens when the runtime interrupts it while it is reacquiring
                // the runtime lock, that must reach the pool so it retires the thread rather than
                // reuse one that has a pending interrupt.
                return this.Runtime.ReleaseControlledThread(this.Operation, this.Worker);
            }

            /// <summary>
            /// Executes this operation on the current thread.
            /// </summary>
            private void Execute() => this.Runtime.ExecuteOperation(
                this.Operation, this.Logic, null, this.PreCondition, this.PostCondition);
        }

        /// <summary>
        /// Returns true if this runtime still associates the specified thread with an operation, or
        /// associates any operation with that thread. Used to verify that abandoning a reserved thread
        /// leaves nothing behind.
        /// </summary>
        internal bool HasMappingsForThread(PooledThread worker) =>
            this.ControlledThreads.ContainsKey(worker.Name) ||
            this.ThreadPool.Values.Contains(worker.OSThread);

        /// <summary>
        /// Releases a thread that was reserved for the specified operation but never given it, and
        /// removes the mappings that were published for the pair.
        /// </summary>
        private void AbandonControlledThread(ControlledOperation op, PooledThread worker)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.RemoveThreadMappings(op, worker.OSThread);
            }

            worker.Release();
        }

        /// <summary>
        /// Releases the pooled thread that finished executing the specified operation, and returns
        /// what that thread must do next.
        /// </summary>
        /// <remarks>
        /// A thread can be reused directly unless this runtime has detached, because
        /// <see cref="Detach"/> is the only place that interrupts a controlled thread, and an interrupt
        /// latches until the thread next waits, so reusing an interrupted thread would raise the
        /// interrupt inside an unrelated operation. Detach assigns the execution status before
        /// interrupting, and does both while holding the runtime lock, so reading the status here under
        /// the same lock is decisive. Either this operation completed first, in which case Detach
        /// skipped it and did not interrupt this thread, or Detach ran first, in which case the status
        /// read below sends the thread through the pool's interrupt drain before it is reused.
        /// </remarks>
        private WorkerDisposition ReleaseControlledThread(ControlledOperation op, PooledThread worker)
        {
            // Checked before entering the section below, which would otherwise mask the condition. A
            // thread that still holds the runtime lock here has leaked it, and reusing it would leave
            // every later operation on it running unsynchronized, because the lock is tracked per thread
            // and a nested enter on a thread that already holds it is a no-op. This must retire the
            // thread rather than drain it: draining is for a latched interrupt, and would park a thread
            // that still owns the runtime lock. It is reported in every configuration, because an
            // assertion alone is compiled out of release builds, which is where such a leak would do
            // the most damage and be the hardest to explain.
            if (IsExecutionSynchronized)
            {
                this.LogWriter.LogError(
                    "[coyote::error] Operation '{0}' released its thread while holding the runtime lock.",
                    op.Name);
                Debug.Assert(false,
                    $"Operation '{op.Name}' released its thread while holding the runtime lock.");
                return WorkerDisposition.Retire;
            }

            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.RemoveThreadMappings(op, worker.OSThread);

                Debug.Assert(op.Status is OperationStatus.Completed,
                    $"Operation '{op.Name}' released its thread without completing.");

                return this.ExecutionStatus is ExecutionStatus.Running ?
                    WorkerDisposition.Reuse : WorkerDisposition.Drain;
            }
        }

        /// <summary>
        /// Starts executing the specified controlled thread with an optional input parameter.
        /// </summary>
        internal void StartControlledThread(Thread thread, ControlledOperation op = null, object input = null)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus is ExecutionStatus.Running)
                {
                    op ??= this.GetOperationExecutingOnThread(thread);
                    if (op is null)
                    {
                        this.NotifyUncontrolledThreadExecution(Thread.CurrentThread);
                    }
                    else
                    {
                        this.StartOperation(op);
                    }

                    if (input is null)
                    {
                        thread.Start();
                    }
                    else
                    {
                        thread.Start(input);
                    }
                }
            }
        }

        /// <summary>
        /// Unwraps the specified task.
        /// </summary>
        internal Task UnwrapTask(Task<Task> task)
        {
            var unwrappedTask = task.Unwrap();
            if (this.ControlledTasks.TryGetValue(task, out ControlledOperation op))
            {
                this.ControlledTasks.TryAdd(unwrappedTask, op);
            }

            return unwrappedTask;
        }

        /// <summary>
        /// Unwraps the specified task.
        /// </summary>
        internal Task<TResult> UnwrapTask<TResult>(Task<Task<TResult>> task)
        {
            var unwrappedTask = task.Unwrap();
            if (this.ControlledTasks.TryGetValue(task, out ControlledOperation op))
            {
                this.ControlledTasks.TryAdd(unwrappedTask, op);
            }

            return unwrappedTask;
        }

        /// <summary>
        /// Registers the specified task as a known controlled task.
        /// </summary>
        internal void RegisterKnownControlledTask(Task task)
        {
            if (this.SchedulingPolicy != SchedulingPolicy.None)
            {
                this.ControlledTasks.TryAdd(task, null);
            }
        }

        /// <summary>
        /// Creates a new controlled operation assigned to the specified optional group.
        /// </summary>
        internal ControlledOperation CreateControlledOperation(OperationGroup group = null)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                // Create a new controlled operation using the next available operation id.
                ulong operationId = this.GetNextOperationId();
                var op = new ControlledOperation(operationId, $"Op({operationId})", group, this);
                if (operationId > 0 && !this.IsThreadControlled(Thread.CurrentThread))
                {
                    op.IsSourceUncontrolled = true;
                }

                return op;
            }
        }

        /// <summary>
        /// Creates a new user-defined controlled operation from the specified builder.
        /// </summary>
        internal ControlledOperation CreateUserDefinedOperation(IOperationBuilder builder)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                // Create a new controlled operation using the next available operation id.
                ulong operationId = this.GetNextOperationId();
                var op = new UserDefinedOperation(this, builder, operationId);
                if (operationId > 0 && !this.IsThreadControlled(Thread.CurrentThread))
                {
                    op.IsSourceUncontrolled = true;
                }

                return op;
            }
        }

        /// <summary>
        /// Registers the specified newly created controlled operation.
        /// </summary>
        /// <param name="op">The newly created operation to register.</param>
        internal void RegisterNewOperation(ControlledOperation op)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus is ExecutionStatus.Running)
                {
                    this.LogWriter.LogDebug("[coyote::debug] Created operation {0} from thread '{1}'.",
                        op.DebugInfo, Thread.CurrentThread.ManagedThreadId);

                    // Assign the operation as a member of its group.
                    op.Group.RegisterMember(op);

#if NETSTANDARD2_0 || NETFRAMEWORK
                    bool isNewOperation = !this.OperationMap.ContainsKey(op.Id);
                    if (isNewOperation)
                    {
                        this.OperationMap.Add(op.Id, op);
                    }
#else
                    bool isNewOperation = this.OperationMap.TryAdd(op.Id, op);
#endif
                    if (isNewOperation)
                    {
                        // Record the registration order and append the operation, which keeps the
                        // collection ordered because this index is the largest assigned so far.
                        op.RegistrationIndex = this.OperationRegistrationCounter++;
                        this.SchedulableOperations.Add(op);
                    }
                }
            }
        }

        /// <summary>
        /// Starts executing the specified operation.
        /// </summary>
        /// <param name="op">The operation to start executing.</param>
        internal void StartOperation(ControlledOperation op)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.LogWriter.LogDebug("[coyote::debug] Started operation {0} from thread '{1}'.",
                    op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                if (this.OperationMap.Count is 1)
                {
                    // This is the first operation registered, so schedule it immediately.
                    this.ScheduledOperation = op;
                }
                else if (this.SchedulingPolicy is SchedulingPolicy.Interleaving && this.OperationMap.Count > 1)
                {
                    // As this is not the first operation getting created, assign an event
                    // handler so that the next scheduling decision cannot be made until
                    // this operation starts executing to avoid race conditions.
                    this.PendingStartOperationMap.Add(op,
                        new ManualResetEventSlim(false, (int)this.Configuration.HandoffSpinCount));
                }
            }
        }

        /// <summary>
        /// Schedules the next enabled operation, which can include the currently executing operation.
        /// </summary>
        /// <param name="current">The currently executing operation, if there is one.</param>
        /// <param name="type">The type of the scheduling point.</param>
        /// <param name="isSuppressible">True if the interleaving can be suppressed, else false.</param>
        /// <param name="isYielding">True if the current operation is yielding, else false.</param>
        /// <returns>True if an operation other than the current was scheduled, else false.</returns>
        /// <remarks>
        /// An enabled operation is one that is not paused nor completed.
        /// </remarks>
        internal bool ScheduleNextOperation(ControlledOperation current, SchedulingPointType type,
            bool isSuppressible = true, bool isYielding = false)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                // Wait for all recently created operations to start executing.
                this.WaitOperationsStart();
                if (this.ExecutionStatus != ExecutionStatus.Running ||
                    this.SchedulingPolicy != SchedulingPolicy.Interleaving)
                {
                    // Cannot schedule the next operation if the scheduler is not attached,
                    // or if the scheduling policy is not systematic.
                    return false;
                }

                // Check if the currently executing thread is uncontrolled.
                bool isThreadUncontrolled = false;
                if (current is null && !this.IsThreadControlled(Thread.CurrentThread))
                {
                    if (this.LastPostponedSchedulingPoint is SchedulingPointType.Pause ||
                        this.LastPostponedSchedulingPoint is SchedulingPointType.Complete)
                    {
                        // A scheduling point was postponed due to a potential deadlock, which has
                        // now been resolved, so resume it on this uncontrolled thread.
                        current = this.ScheduledOperation;
                        type = this.LastPostponedSchedulingPoint.Value;
                        this.LogWriter.LogDebug("[coyote::debug] Resuming scheduling point '{0}' of operation {1} in uncontrolled thread '{2}'.",
                            type, current.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                    }
                    else if (type is SchedulingPointType.Create || type is SchedulingPointType.ContinueWith)
                    {
                        // This is a scheduling point that was invoked because a new operation was
                        // created by an uncontrolled thread, so postpone the scheduling point and
                        // resume it on the next available controlled thread.
                        this.LogWriter.LogDebug("[coyote::debug] Postponing scheduling point '{0}' in uncontrolled thread '{1}'.",
                            type, Thread.CurrentThread.ManagedThreadId);
                        this.LastPostponedSchedulingPoint = type;
                        return false;
                    }

                    isThreadUncontrolled = true;
                }

                // If the current operation was provided as argument to this method, or it is null, then this
                // is a controlled thread, so get the currently executing operation to proceed with scheduling.
                current ??= this.GetExecutingOperation();
                if (current is null)
                {
                    // Cannot proceed without having access to the currently executing operation.
                    return false;
                }

                if (current != this.ScheduledOperation)
                {
                    // The currently executing operation is not scheduled, so send it to sleep.
                    this.PauseOperation(current);
                    return false;
                }

                if (this.ScheduleSuppressionCount > 0 && this.LastPostponedSchedulingPoint is null &&
                    isSuppressible && current.Status is OperationStatus.Enabled)
                {
                    // Suppress the scheduling point.
                    this.LogWriter.LogDebug("[coyote::debug] Operation {0} suppressed scheduling point '{1}'.",
                        current.DebugInfo, type);
                    return false;
                }

                this.LogWriter.LogDebug(
                    "[coyote::debug] Operation {0} reached scheduling point '{1}' at execution step '{2}' on thread '{3}'.",
                    current.DebugInfo, type, this.Scheduler.StepCount, Thread.CurrentThread.ManagedThreadId);
                this.Assert(!this.IsSpecificationInvoked, "Executing a specification monitor must be atomic.");

                // Checks if the scheduling steps bound has been reached.
                this.CheckIfSchedulingStepsBoundIsReached();

                // Update metadata related to this scheduling point.
                current.LastSchedulingPoint = type;
                this.LastPostponedSchedulingPoint = null;

                // Update the current operation with the hashed program state.
                current.LastHashedProgramState = this.ComputeProgramState();

                // Try to enable any operations with resolved dependencies before asking the
                // scheduler to choose the next one to schedule. This also drops any operations
                // that completed since the previous scheduling step.
                IReadOnlyList<ControlledOperation> ops = this.SchedulableOperations;
                this.AssertSchedulableOperationsInvariant();
                if (!this.TryEnableOperationsWithResolvedDependencies(current))
                {
                    if (this.IsUncontrolledConcurrencyDetected &&
                        this.Configuration.IsPartiallyControlledConcurrencyAllowed)
                    {
                        // TODO: optimize and make this more fine-grained.
                        // If uncontrolled concurrency is detected, then do not check for deadlocks directly,
                        // but instead leave it to the background deadlock detection timer and postpone the
                        // scheduling point, which might get resolved from an uncontrolled thread.
                        this.LogWriter.LogDebug("[coyote::debug] Postponing scheduling point '{0}' of operation {1} due to potential deadlock.",
                            type, current.DebugInfo);
                        this.LastPostponedSchedulingPoint = type;
                        this.PauseOperation(current);
                        return false;
                    }

                    // Check if the execution has deadlocked.
                    this.CheckIfExecutionHasDeadlocked(ops);
                }

                if (this.Configuration.IsLivenessCheckingEnabled && this.Scheduler.IsIterationFair)
                {
                    // Check if the liveness threshold has been reached if scheduling is fair.
                    this.CheckLivenessThresholdExceeded();
                }

                if (this.Configuration.IsScheduleCoverageReported)
                {
                    this.CoverageInfo.DeclareSchedulingPoint(type.ToString(), new StackTrace().ToString());
                }

                if (!this.Scheduler.GetNextOperation(ops, current, isYielding, out ControlledOperation next))
                {
                    // The scheduler hit the scheduling steps bound.
                    this.Detach(ExecutionStatus.BoundReached);
                    return false;
                }

                this.LogWriter.LogDebug("[coyote::debug] Scheduling operation {0} from thread '{1}'.",
                    next.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                bool isNextOperationScheduled = current != next;
                if (isNextOperationScheduled)
                {
                    // Pause the currently scheduled operation, and enable the next one.
                    this.ScheduledOperation = next;
                    next.Signal();
                    this.PauseOperation(current);
                }
                else if (isThreadUncontrolled)
                {
                    // If the current operation is the next operation to schedule, and the current thread
                    // is uncontrolled, then we need to signal the current operation to resume execution.
                    next.Signal();
                }

                return isNextOperationScheduled;
            }
        }

        /// <summary>
        /// Pauses the execution of the specified operation.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method is invoked by the same thread executing the operation
        /// and that it runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private void PauseOperation(ControlledOperation op)
        {
            // Only pause the operation if it is not already completed and it is currently executing on this thread.
            if (op.Status != OperationStatus.Completed && op == ExecutingOperation)
            {
                // Do not allow the operation to wake up, unless its currently scheduled and enabled or the runtime stopped running.
                while (!(op == this.ScheduledOperation && op.Status is OperationStatus.Enabled) && this.ExecutionStatus is ExecutionStatus.Running)
                {
                    this.LogWriter.LogDebug("[coyote::debug] Sleeping operation {0} on thread '{1}'.",
                        op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                    using (SynchronizedSection.Exit(this.RuntimeLock))
                    {
                        op.WaitSignal();
                    }

                    this.LogWriter.LogDebug("[coyote::debug] Waking up operation {0} on thread '{1}'.",
                        op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                }
            }
        }

        /// <summary>
        /// Pauses the currently executing operation until the specified condition gets resolved.
        /// </summary>
        internal void PauseOperationUntil(ControlledOperation current, Func<bool> condition, bool isConditionControlled = true, string debugMsg = null)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    // Only proceed if there is an operation executing on the current thread and
                    // the condition is not already resolved.
                    current ??= this.GetExecutingOperation();
                    while (current != null && !condition() && this.ExecutionStatus is ExecutionStatus.Running)
                    {
                        this.LogWriter.LogDebug("[coyote::debug] Operation {0} is waiting for {1} on thread '{2}'.",
                            current.DebugInfo, debugMsg ?? "condition to get resolved", Thread.CurrentThread.ManagedThreadId);
                        // TODO: can we identify when the dependency is uncontrolled?
                        current.PauseWithDependency(condition, isConditionControlled);
                        this.ScheduleNextOperation(current, SchedulingPointType.Pause);
                    }
                }
            }
        }

        /// <summary>
        /// Asynchronously pauses the currently executing operation until the specified condition gets resolved.
        /// </summary>
        internal PausedOperationAwaitable PauseOperationUntilAsync(Func<bool> condition, bool resumeAsynchronously)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                    this.TryGetExecutingOperation(out ControlledOperation current))
                {
                    return new PausedOperationAwaitable(this, current, condition, resumeAsynchronously);
                }
            }

            return new PausedOperationAwaitable(this, null, condition, resumeAsynchronously);
        }

        /// <summary>
        /// Delays the currently executing operation for a non-deterministically chosen amount of time.
        /// </summary>
        /// <remarks>
        /// The delay is chosen non-deterministically by an underlying fuzzing strategy.
        /// If a delay of 0 is chosen, then the operation is not delayed.
        /// </remarks>
        internal void DelayOperation(ControlledOperation current)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus != ExecutionStatus.Running)
                {
                    throw new ThreadInterruptedException();
                }

                if (current != null || this.TryGetExecutingOperation(out current))
                {
                    // Choose the next delay to inject. The value is in milliseconds.
                    int delay = this.GetNondeterministicDelay(current, (int)this.Configuration.MaxFuzzingDelay);
                    this.LogWriter.LogDebug("[coyote::debug] Delaying operation {0} on thread '{1}' by {2}ms.",
                        current.DebugInfo, Thread.CurrentThread.ManagedThreadId, delay);

                    // Only sleep the executing operation if a non-zero delay was chosen.
                    if (delay > 0)
                    {
                        var previousStatus = current.Status;
                        current.Status = OperationStatus.PausedOnDelay;
                        using (SynchronizedSection.Exit(this.RuntimeLock))
                        {
                            Thread.SpinWait(delay);
                        }

                        current.Status = previousStatus;
                    }
                }
            }
        }

        /// <summary>
        /// Waits for all recently created operations to start executing.
        /// </summary>
        /// <remarks>
        /// This method performs a handshake with <see cref="OnStarted"/>. It is assumed that this
        /// method is invoked by the same thread executing the operation and that it runs in the
        /// scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private void WaitOperationsStart()
        {
            if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                while (this.PendingStartOperationMap.Count > 0)
                {
                    var pendingOp = this.PendingStartOperationMap.First();
                    while (pendingOp.Key.Status is OperationStatus.None)
                    {
                        this.LogWriter.LogDebug("[coyote::debug] Sleeping thread '{0}' until operation {1} starts.",
                            Thread.CurrentThread.ManagedThreadId, pendingOp.Key.DebugInfo);
                        using (SynchronizedSection.Exit(this.RuntimeLock))
                        {
                            try
                            {
                                pendingOp.Value.Wait();
                            }
                            catch (ObjectDisposedException)
                            {
                                // The handler was disposed, so we can ignore this exception.
                            }
                        }

                        this.LogWriter.LogDebug("[coyote::debug] Waking up thread '{0}'.", Thread.CurrentThread.ManagedThreadId);
                    }

                    pendingOp.Value.Dispose();
                    this.PendingStartOperationMap.Remove(pendingOp.Key);
                }
            }
        }

        /// <summary>
        /// Notifies that the specified controlled operation started executing.
        /// </summary>
        /// <param name="op">The operation that started executing.</param>
        /// <remarks>
        /// This method performs a handshake with <see cref="WaitOperationsStart"/>.
        /// </remarks>
        internal void OnStarted(ControlledOperation op)
        {
            // Configures the execution context of the current thread with data
            // related to the runtime and the operation executed by this thread.
            this.SetCurrentExecutionContext(op);
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.LogWriter.LogDebug("[coyote::debug] Operation {0} started executing on thread '{1}'.",
                    op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                op.Status = OperationStatus.Enabled;
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    // If this operation has an associated handler that notifies another awaiting
                    // operation about this operation starting its execution, then set the handler.
                    if (this.PendingStartOperationMap.TryGetValue(op, out ManualResetEventSlim handler))
                    {
                        handler.Set();
                    }

                    // Pause the operation as soon as it starts executing to allow the runtime
                    // to explore a potential interleaving with another executing operation.
                    this.PauseOperation(op);
                }
            }
        }

        /// <summary>
        /// Notifies that the specified controlled operation completed executing.
        /// </summary>
        /// <param name="op">The operation that completed executing.</param>
        internal void OnCompleted(ControlledOperation op)
        {
            op.ExecuteContinuations();
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.LogWriter.LogDebug("[coyote::debug] Operation {0} completed on thread '{1}'.",
                    op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                op.Status = OperationStatus.Completed;
            }
        }

        /// <summary>
        /// Tries to reset the specified controlled operation so that it can start executing again.
        /// This is only allowed if the operation is already completed.
        /// </summary>
        /// <param name="op">The operation to reset.</param>
        internal bool TryResetOperation(ControlledOperation op)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (op.Status is OperationStatus.Completed)
                {
                    this.LogWriter.LogDebug("[coyote::debug] Resetting operation {0} from thread '{1}'.",
                        op.DebugInfo, Thread.CurrentThread.ManagedThreadId);
                    op.Status = OperationStatus.None;

                    // The operation is schedulable again, so restore it at its original position if
                    // a previous scheduling step already removed it.
                    this.AddSchedulableOperation(op);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds the specified operation to <see cref="SchedulableOperations"/> at the position
        /// given by its registration order, unless it is already present.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private void AddSchedulableOperation(ControlledOperation op)
        {
            // Binary search over the registration order that the list is kept sorted by. Registration
            // indexes are unique, so a match means the operation is already present.
            int index = this.SchedulableOperations.BinarySearch(op, RegistrationOrderComparer);
            if (index < 0)
            {
                this.SchedulableOperations.Insert(~index, op);
            }
        }

        /// <summary>
        /// Suppresses scheduling points until <see cref="ResumeScheduling"/> is invoked,
        /// unless a scheduling point must occur naturally.
        /// </summary>
        internal void SuppressScheduling()
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.LogWriter.LogDebug("[coyote::debug] Suppressing scheduling of enabled operations in runtime '{0}'.", this.Id);
                this.ScheduleSuppressionCount++;
            }
        }

        /// <summary>
        /// Resumes scheduling points that were suppressed by invoking <see cref="SuppressScheduling"/>.
        /// </summary>
        internal void ResumeScheduling()
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.LogWriter.LogDebug("[coyote::debug] Resuming scheduling of enabled operations in runtime '{0}'.", this.Id);
                if (this.ScheduleSuppressionCount > 0)
                {
                    this.ScheduleSuppressionCount--;
                }
            }
        }

        /// <summary>
        /// Sets a checkpoint in the currently explored execution trace, that allows replaying all
        /// scheduling decisions until the checkpoint in subsequent iterations.
        /// </summary>
        internal void CheckpointExecutionTrace()
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                ExecutionTrace trace = this.Scheduler.CheckpointExecutionTrace();
                this.LogWriter.LogDebug("[coyote::debug] Set checkpoint in current execution path with length '{0}' in runtime '{1}'.",
                    trace.Length, this.Id);
            }
        }

        /// <inheritdoc/>
        public bool RandomBoolean() => this.GetNextNondeterministicBooleanChoice(null, null);

        /// <summary>
        /// Returns the next nondeterministic boolean choice.
        /// </summary>
        internal bool GetNextNondeterministicBooleanChoice(string callerName, string callerType)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                bool result;
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    // Checks if the current operation is controlled by the runtime.
                    this.GetExecutingOperation();

                    // Checks if the scheduling steps bound has been reached.
                    this.CheckIfSchedulingStepsBoundIsReached();

                    if (this.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                        this.Configuration.IsLivenessCheckingEnabled && this.Scheduler.IsIterationFair)
                    {
                        // Check if the liveness threshold has been reached if scheduling is fair.
                        this.CheckLivenessThresholdExceeded();
                    }

                    // Update the current operation with the hashed program state.
                    this.ScheduledOperation.LastHashedProgramState = this.ComputeProgramState();

                    if (!this.Scheduler.GetNextBoolean(this.ScheduledOperation, out result))
                    {
                        this.Detach(ExecutionStatus.BoundReached);
                    }
                }
                else
                {
                    result = this.ValueGenerator.Next(2) is 0 ? true : false;
                }

                this.LogManager.LogRandom(result, callerName, callerType);
                return result;
            }
        }

        /// <inheritdoc/>
        public int RandomInteger(int maxValue) => this.GetNextNondeterministicIntegerChoice(maxValue, null, null);

        /// <summary>
        /// Returns the next nondeterministic integer choice.
        /// </summary>
        internal int GetNextNondeterministicIntegerChoice(int maxValue, string callerName, string callerType)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                int result;
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    // Checks if the current operation is controlled by the runtime.
                    this.GetExecutingOperation();

                    // Checks if the scheduling steps bound has been reached.
                    this.CheckIfSchedulingStepsBoundIsReached();

                    if (this.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                        this.Configuration.IsLivenessCheckingEnabled && this.Scheduler.IsIterationFair)
                    {
                        // Check if the liveness threshold has been reached if scheduling is fair.
                        this.CheckLivenessThresholdExceeded();
                    }

                    // Update the current operation with the hashed program state.
                    this.ScheduledOperation.LastHashedProgramState = this.ComputeProgramState();

                    if (!this.Scheduler.GetNextInteger(this.ScheduledOperation, maxValue, out result))
                    {
                        this.Detach(ExecutionStatus.BoundReached);
                    }
                }
                else
                {
                    result = this.ValueGenerator.Next(maxValue);
                }

                this.LogManager.LogRandom(result, callerName, callerType);
                return result;
            }
        }

        /// <summary>
        /// Returns a controlled nondeterministic delay for the specified operation.
        /// </summary>
        private int GetNondeterministicDelay(ControlledOperation op, int maxDelay)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                // Checks if the scheduling steps bound has been reached.
                this.CheckIfSchedulingStepsBoundIsReached();

                // Choose the next delay to inject.
                if (!this.Scheduler.GetNextDelay(op, maxDelay, out int next))
                {
                    this.Detach(ExecutionStatus.BoundReached);
                }

                return next;
            }
        }

        /// <summary>
        /// Tries to enable any operations that have their dependencies resolved. It returns
        /// true if there is at least one operation enabled, else false.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private bool TryEnableOperationsWithResolvedDependencies(ControlledOperation current)
        {
            this.LogWriter.LogDebug("[coyote::debug] Trying to enable any operation with resolved dependencies in runtime '{0}'.", this.Id);

            int attempt = 0;
            int delay = (int)this.Configuration.UncontrolledConcurrencyResolutionDelay;
            uint maxAttempts = this.Configuration.UncontrolledConcurrencyResolutionAttempts;
            uint enabledOpsCount = 0;
            while (true)
            {
                // Cache the count of enabled operations from the previous attempt.
                uint previousEnabledOpsCount = enabledOpsCount;
                enabledOpsCount = 0;

                uint statusChanges = 0;
                bool isRootDependencyUnresolved = false;
                bool isAnyDependencyUnresolved = false;

                // Drop the operations that have completed since the last scheduling step. This
                // runs as its own pass, before the walk below rather than fused into it, because
                // the walk invokes the dependency predicate of every paused operation, and such a
                // predicate is arbitrary user code (see Operation.PauseUntil): registering or
                // resetting an operation from inside it inserts into the very collection that an
                // in-place compaction is rewriting.
                this.CompactSchedulableOperations();

                // Whether some operation is enabled is only consulted for delayed operations, and
                // it can only go from false to true while this walk runs, because the only status
                // transitions it performs are into 'Enabled'. So it is enough to establish it once
                // up front and then keep it up to date as operations become enabled, instead of
                // rescanning every operation for each delayed one.
                bool isAnyOperationEnabled = IsAnyOperationEnabled(this.SchedulableOperations);

                // A dependency predicate is arbitrary user code that can add to the collection while
                // this walk runs, in either of two ways. Registering appends, because the new
                // operation takes the largest registration index. Resetting inserts at the reset
                // operation's original sorted position, which can be BEFORE the current index; every
                // later entry then shifts right, so advancing the index lands back on an entry that
                // was already visited. The count is therefore re-read on each step, and the
                // registration index is carried as a watermark so that each operation is visited
                // exactly once however the collection moved underneath.
                //
                // Skipping an inserted operation is correct rather than merely tolerable: a reset
                // sets the status to 'None', which this walk neither enables nor counts, and a newly
                // registered operation is not yet paused, so neither has anything to contribute to
                // this pass. Revisiting one, on the other hand, would count it as enabled twice.
                int lastVisited = -1;
                for (int idx = 0; idx < this.SchedulableOperations.Count; ++idx)
                {
                    var op = this.SchedulableOperations[idx];
                    if (op.RegistrationIndex <= lastVisited)
                    {
                        // An insert shifted this entry to a position the walk has already passed.
                        continue;
                    }

                    lastVisited = op.RegistrationIndex;
                    if (op.Status is OperationStatus.Completed)
                    {
                        continue;
                    }

                    var previousStatus = op.Status;
                    if (op.IsPaused)
                    {
                        TryEnableOperation(op, isAnyOperationEnabled);
                        if (previousStatus == op.Status)
                        {
                            this.LogWriter.LogDebug("[coyote::debug] Operation {0} has status '{1}'.", op.DebugInfo, op.Status);
                            if (op.IsPaused && op.IsDependencyUncontrolled)
                            {
                                if (op.IsRoot)
                                {
                                    isRootDependencyUnresolved = true;
                                }
                                else
                                {
                                    isAnyDependencyUnresolved = true;
                                }
                            }
                        }
                        else
                        {
                            this.LogWriter.LogDebug("[coyote::debug] Operation {0} changed status from '{1}' to '{2}'.",
                                op.DebugInfo, previousStatus, op.Status);
                            statusChanges++;
                        }
                    }

                    if (op.Status is OperationStatus.Enabled)
                    {
                        enabledOpsCount++;
                        isAnyOperationEnabled = true;
                    }
                }

                // Heuristics for handling a partially controlled execution.
                if (this.IsUncontrolledConcurrencyDetected &&
                    this.Configuration.IsPartiallyControlledConcurrencyAllowed)
                {
                    // Compute the delta of enabled operations from the previous attempt.
                    uint enabledOpsDelta = attempt is 0 ? 0 : enabledOpsCount - previousEnabledOpsCount;

                    // This value is true if the current operation just completed and has uncontrolled source.
                    bool isSourceUnresolved = current.Status is OperationStatus.Completed && current.IsSourceUncontrolled;

                    // We consider the concurrency to be unresolved if there were no new enabled operations
                    // or status changes in this attempt, and one of the following cases holds:
                    // - If there are no enabled operations, then the concurrency is unresolved if
                    //   the current operation was just completed and has uncontrolled source, or
                    //   if there are any unresolved dependencies.
                    // - If there are enabled operations, then the concurrency is unresolved if
                    //   there are any (non-root) unresolved dependencies.
                    bool isNoEnabledOpsCaseResolved = enabledOpsCount is 0 &&
                        (isSourceUnresolved || isAnyDependencyUnresolved || isRootDependencyUnresolved);
                    bool isSomeEnabledOpsCaseResolved = enabledOpsCount > 0 && isAnyDependencyUnresolved;
                    bool isConcurrencyUnresolved = enabledOpsDelta is 0 && statusChanges is 0 &&
                        (isNoEnabledOpsCaseResolved || isSomeEnabledOpsCaseResolved);

                    // Retry if there is unresolved concurrency and attempts left.
                    if (++attempt < maxAttempts && isConcurrencyUnresolved)
                    {
                        // Implement a simple retry logic to try resolve uncontrolled concurrency.
                        this.LogWriter.LogDebug("[coyote::debug] Pausing controlled thread '{0}' to try resolve uncontrolled concurrency.",
                            Thread.CurrentThread.ManagedThreadId);
                        using (SynchronizedSection.Exit(this.RuntimeLock))
                        {
                            Thread.SpinWait(delay);
                        }

                        continue;
                    }
                }

                break;
            }

            this.LogWriter.LogDebug("[coyote::debug] There are {0} enabled operations in runtime '{1}'.",
                enabledOpsCount, this.Id);
            this.MaxConcurrencyDegree = Math.Max(this.MaxConcurrencyDegree, enabledOpsCount);
            return enabledOpsCount > 0;
        }

        /// <summary>
        /// Removes from <see cref="SchedulableOperations"/> the operations that have completed.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// Removal is lazy: an operation that completes after this returns is only dropped by the
        /// next sweep. Writing the survivors forward in place preserves the registration order
        /// that the collection is kept sorted by. This must invoke nothing that can register or
        /// reset an operation, because either would insert into the collection being rewritten.
        /// </remarks>
        private void CompactSchedulableOperations()
        {
            int writeIndex = 0;
            for (int readIndex = 0; readIndex < this.SchedulableOperations.Count; ++readIndex)
            {
                var op = this.SchedulableOperations[readIndex];
                if (op.Status != OperationStatus.Completed)
                {
                    this.SchedulableOperations[writeIndex++] = op;
                }
            }

            this.SchedulableOperations.RemoveRange(writeIndex, this.SchedulableOperations.Count - writeIndex);
        }

        /// <summary>
        /// Checks the invariants of <see cref="SchedulableOperations"/> against <see cref="OperationMap"/>.
        /// </summary>
        /// <remarks>
        /// Only compiled into debug builds. This is not an equality check: removal is lazy, so the
        /// collection may still hold an operation that completed since the last sweep. What must
        /// hold is that it contains every operation that is still schedulable, that it never
        /// contains anything unregistered, and that it presents them in registration order.
        /// <para>
        /// The two directions are checked at different rates. Everything here costs O(schedulable
        /// operations), which a scheduling step already pays, except the sweep over the operation
        /// map, which costs O(operations ever created) — the very cost that keeping this collection
        /// exists to keep off the per-step path. Paying it on every step would make a debug run
        /// quadratic in the operation count, so it is amortized over a stride instead: a missing
        /// operation persists until it is scheduled again, so a periodic check still catches it.
        /// </para>
        /// </remarks>
        [Conditional("DEBUG")]
        private void AssertSchedulableOperationsInvariant()
        {
            // Note that the assertion messages are only formatted once an invariant is violated,
            // because this runs on every scheduling step of a debug build.
            for (int idx = 0; idx < this.SchedulableOperations.Count; ++idx)
            {
                var op = this.SchedulableOperations[idx];
                if (!this.OperationMap.ContainsKey(op.Id))
                {
                    Debug.Fail($"Operation {op.DebugInfo} is schedulable but was never registered.");
                }

                // Registration indexes are unique, so a strictly increasing order also rules out
                // the same operation being present twice.
                Debug.Assert(idx is 0 ||
                    this.SchedulableOperations[idx - 1].RegistrationIndex < op.RegistrationIndex,
                    "The schedulable operations are not ordered by registration index.");
            }

            if (this.Scheduler.StepCount % SchedulableOperationsAuditStride is 0)
            {
                var schedulable = new HashSet<ControlledOperation>(this.SchedulableOperations);
                foreach (var op in this.OperationMap.Values)
                {
                    if (op.Status != OperationStatus.Completed && !schedulable.Contains(op))
                    {
                        Debug.Fail($"Operation {op.DebugInfo} has status '{op.Status}' but is not schedulable.");
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if any of the specified operations is currently enabled, else false.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private static bool IsAnyOperationEnabled(IReadOnlyList<ControlledOperation> ops)
        {
            for (int idx = 0; idx < ops.Count; ++idx)
            {
                if (ops[idx].Status is OperationStatus.Enabled)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Tries to enable the specified operation, if its dependencies have been resolved.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        /// <param name="op">The operation to try enable.</param>
        /// <param name="isAnyOperationEnabled">True if some operation is currently enabled, else false.</param>
        private static bool TryEnableOperation(ControlledOperation op, bool isAnyOperationEnabled)
        {
            if (op.Status is OperationStatus.PausedOnDelay ||
                op.Status is OperationStatus.PausedOnResourceOrDelay)
            {
                if (op.Status is OperationStatus.PausedOnDelay &&
                    op.DelayCancellationToken.IsCancellationRequested)
                {
                    op.EnableAfterDelay();
                    return true;
                }

                if (op.DelayedStepsCount > 0)
                {
                    op.DelayedStepsCount--;
                }

                // The operation is delayed, so it is enabled either if the delay completes
                // or if no other operation is enabled.
                //
                // PausedOnResourceOrDelay takes the SAME path: it is a resource wait carrying a finite
                // timeout, so reaching here means the timeout fired rather than a signal arriving. Clearing
                // the awaited resources is what tells the waiter apart from a resource wake — it observes a
                // zero budget and reports a timeout. The "no other operation is enabled" escape is what
                // makes a real timeout fire instead of the program deadlocking, and it is also what keeps
                // the scheduler's step count advancing so the periodic hang monitor stays satisfied.
                if (op.DelayedStepsCount is 0 || !isAnyOperationEnabled)
                {
                    op.EnableAfterDelay();
                    return true;
                }

                return false;
            }

            // If the operation is paused, then check if its dependency has been resolved.
            return op.TryEnable();
        }

        /// <summary>
        /// Pauses the scheduled controlled operation until either the uncontrolled condition resolves, the
        /// corresponding logic tries to invoke an uncontrolled scheduling point, or the timeout expires.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private void TryPauseAndResolveUncontrolledCondition(Func<bool> condition)
        {
            if (this.IsThreadControlled(Thread.CurrentThread))
            {
                // A scheduling point from an uncontrolled thread has not been postponed yet, so pause the execution
                // of the current operation to try give time to the uncontrolled concurrency to be resolved.
                if (this.LastPostponedSchedulingPoint is null)
                {
                    int attempt = 0;
                    int delay = (int)this.Configuration.UncontrolledConcurrencyResolutionDelay;
                    uint maxAttempts = this.Configuration.UncontrolledConcurrencyResolutionAttempts;
                    while (attempt++ < maxAttempts && !condition())
                    {
                        this.LogWriter.LogDebug("[coyote::debug] Pausing controlled thread '{0}' to try resolve uncontrolled concurrency.",
                            Thread.CurrentThread.ManagedThreadId);
                        using (SynchronizedSection.Exit(this.RuntimeLock))
                        {
                            Thread.SpinWait(delay);
                        }

                        if (this.LastPostponedSchedulingPoint.HasValue)
                        {
                            // A scheduling point from an uncontrolled thread has been postponed,
                            // so stop trying to resolve the uncontrolled concurrency.
                            break;
                        }
                    }
                }

                if (this.LastPostponedSchedulingPoint.HasValue)
                {
                    this.LogWriter.LogDebug("[coyote::debug] Resuming controlled thread '{0}' with uncontrolled concurrency resolved.",
                        Thread.CurrentThread.ManagedThreadId);
                    this.ScheduleNextOperation(default, this.LastPostponedSchedulingPoint.Value, isSuppressible: false);
                }
            }
        }

        /// <summary>
        /// Returns the currently executing <see cref="ControlledOperation"/>,
        /// or null if no such operation is executing.
        /// </summary>
        /// <remarks>
        /// Invoking this method checks if the current thread is uncontrolled or not.
        /// </remarks>
        internal ControlledOperation GetExecutingOperation()
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                var op = ExecutingOperation;
                if (op is null)
                {
                    this.NotifyUncontrolledThreadExecution(Thread.CurrentThread);
                }

                return op;
            }
        }

        /// <summary>
        /// Returns the currently executing <see cref="ControlledOperation"/> of the
        /// specified type, or null if no such operation is executing.
        /// </summary>
        /// <remarks>
        /// Invoking this method checks if the current thread is uncontrolled or not.
        /// </remarks>
        internal TControlledOperation GetExecutingOperation<TControlledOperation>()
            where TControlledOperation : ControlledOperation
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                var op = ExecutingOperation;
                if (op is null)
                {
                    this.NotifyUncontrolledThreadExecution(Thread.CurrentThread);
                }

                return op is TControlledOperation expected ? expected : default;
            }
        }

        /// <summary>
        /// Returns the currently executing <see cref="ControlledOperation"/>,
        /// or null if no such operation is executing.
        /// </summary>
        internal ControlledOperation GetExecutingOperationUnsafe()
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                return ExecutingOperation;
            }
        }

        /// <summary>
        /// Returns the <see cref="ControlledOperation"/> executing on the current thread,
        /// or null if there is none, without acquiring the runtime lock.
        /// </summary>
        /// <remarks>
        /// The backing field is thread-static, so a thread reading its own slot requires no
        /// synchronization. Unlike <see cref="GetExecutingOperation"/> this performs no
        /// uncontrolled-thread notification, so it must only be used by callers that either
        /// have no need for that side effect or fall back to the notifying accessor when
        /// this returns null.
        /// </remarks>
        internal static ControlledOperation GetExecutingOperationUnsynchronized() => ExecutingOperation;

        /// <summary>
        /// Tries to return the currently executing <see cref="ControlledOperation"/>,
        /// or false if no such operation is executing.
        /// </summary>
        internal bool TryGetExecutingOperation(out ControlledOperation op)
        {
            op = this.GetExecutingOperation();
            return op != null;
        }

        /// <summary>
        /// Returns the <see cref="ControlledOperation"/> executing on the specified
        /// controlled thread, or null if no such operation exists.
        /// </summary>
        internal ControlledOperation GetOperationExecutingOnThread(Thread thread)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                ControlledOperation op = null;
                string name = thread?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    this.ControlledThreads.TryGetValue(name, out op);
                }

                return op;
            }
        }

        /// <summary>
        /// Returns the <see cref="ControlledOperation"/> associated with the specified
        /// operation id, or null if no such operation exists.
        /// </summary>
        internal ControlledOperation GetOperationWithId(ulong operationId)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.OperationMap.TryGetValue(operationId, out ControlledOperation op);
                return op;
            }
        }

        /// <summary>
        /// Returns the <see cref="ControlledOperation"/> of the specified type that is associated
        /// with the specified operation id, or null if no such operation exists.
        /// </summary>
        internal TControlledOperation GetOperationWithId<TControlledOperation>(ulong operationId)
            where TControlledOperation : ControlledOperation
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.OperationMap.TryGetValue(operationId, out ControlledOperation op) &&
                    op is TControlledOperation expected)
                {
                    return expected;
                }
            }

            return default;
        }

        /// <summary>
        /// Returns the next available unique operation id.
        /// </summary>
        internal ulong GetNextOperationId() =>
            // Atomically increments and safely wraps the value into an unsigned long.
            (ulong)Interlocked.Increment(ref this.OperationIdCounter) - 1;

        /// <summary>
        /// Registers a new state hashing function that contributes to computing
        /// a representation of the program state in each scheduling step.
        /// </summary>
        internal void RegisterStateHashingFunction(Func<int> func)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                this.StateHashingFunctions.Add(func);
            }
        }

        /// <summary>
        /// Returns the current program state represented by a hash.
        /// </summary>
        /// <remarks>
        /// The hash is updated in each execution step.
        /// </remarks>
        private int ComputeProgramState()
        {
            unchecked
            {
                int hash = 19;
                bool isStateHashed = false;

                // Asking the scheduler rather than the configuration, because the answer depends on
                // the strategy running this iteration as well as on what the user asked for. Under
                // the default portfolio only q-learning reads the result, so the other four
                // iterations out of five skip the walk below entirely.
                if (this.Scheduler.IsImplicitProgramStateHashingEnabled)
                {
                    isStateHashed = true;

                    // By default every registered operation contributes, including the completed
                    // ones, so that the state distinguishes how far the execution has progressed.
                    // Restricting this to the operations that are still schedulable makes the cost
                    // of a scheduling step independent of how many operations have already
                    // completed, at the price of a coarser state.
                    if (this.Configuration.IsLiveOperationStateHashingEnabled)
                    {
                        // Completed operations are skipped explicitly rather than relied upon to be
                        // absent. This runs before the sweep that drops them, so the collection still
                        // holds everything that completed since the previous scheduling step, and
                        // letting those contribute would make the hash depend on when the sweep ran
                        // rather than on the state of the program.
                        for (int idx = 0; idx < this.SchedulableOperations.Count; ++idx)
                        {
                            var operation = this.SchedulableOperations[idx];
                            if (operation.Status != OperationStatus.Completed)
                            {
                                hash *= 31 + operation.GetHashedState(this.SchedulingPolicy);
                            }
                        }
                    }
                    else
                    {
                        foreach (var operation in this.OperationMap.Values)
                        {
                            hash *= 31 + operation.GetHashedState(this.SchedulingPolicy);
                        }
                    }

                    foreach (var monitor in this.SpecificationMonitors)
                    {
                        hash *= 31 + monitor.GetHashedState();
                    }
                }

                if (this.StateHashingFunctions.Count > 0)
                {
                    isStateHashed = true;
                    int customHash = 19;
                    foreach (var func in this.StateHashingFunctions)
                    {
                        customHash *= 31 + func();
                    }

                    hash *= 31 + customHash;
                }

                if (isStateHashed)
                {
                    // Only record the state when something actually contributed to the hash.
                    // Otherwise it is a constant, so the visited state set degenerates to a
                    // meaningless singleton, and recording it takes a lock on every
                    // scheduling step to no purpose.
                    this.CoverageInfo.DeclareVisitedState(hash);
                }

                return hash;
            }
        }

        /// <inheritdoc/>
        public void RegisterMonitor<T>()
            where T : SpecMonitor =>
            this.TryCreateMonitor(typeof(T));

        /// <summary>
        /// Tries to create a new <see cref="SpecMonitor"/> of the specified <see cref="Type"/>.
        /// </summary>
        private bool TryCreateMonitor(Type type)
        {
            if (this.SchedulingPolicy != SchedulingPolicy.None ||
                this.Configuration.IsMonitoringEnabledOutsideTesting)
            {
                using (SynchronizedSection.Enter(this.RuntimeLock))
                {
                    // Only one monitor per type is allowed.
                    if (!this.SpecificationMonitors.Any(m => m.GetType() == type))
                    {
                        var monitor = (SpecMonitor)Activator.CreateInstance(type);
                        monitor.Initialize(this.Configuration, this);
                        monitor.InitializeStateInformation();
                        this.SpecificationMonitors.Add(monitor);
                        if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                        {
                            this.SuppressScheduling();
                            this.IsSpecificationInvoked = true;
                            monitor.GotoStartState();
                            this.IsSpecificationInvoked = false;
                            this.ResumeScheduling();
                        }
                        else
                        {
                            monitor.GotoStartState();
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public void Monitor<T>(SpecMonitor.Event e)
            where T : SpecMonitor =>
            this.InvokeMonitor(typeof(T), e, null, null, null);

        /// <summary>
        /// Invokes the specified <see cref="SpecMonitor"/> with the specified <see cref="SpecMonitor.Event"/>.
        /// </summary>
        internal void InvokeMonitor(Type type, SpecMonitor.Event e, string senderName, string senderType, string senderStateName)
        {
            if (this.SchedulingPolicy != SchedulingPolicy.None ||
                this.Configuration.IsMonitoringEnabledOutsideTesting)
            {
                using (SynchronizedSection.Enter(this.RuntimeLock))
                {
                    SpecMonitor monitor = null;
                    foreach (var m in this.SpecificationMonitors)
                    {
                        if (m.GetType() == type)
                        {
                            monitor = m;
                            break;
                        }
                    }

                    if (monitor != null)
                    {
                        this.Assert(e != null, "Cannot invoke monitor '{0}' with a null event.", type.FullName);
                        if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                        {
                            this.SuppressScheduling();
                            this.IsSpecificationInvoked = true;
                            monitor.MonitorEvent(e, senderName, senderType, senderStateName);
                            this.IsSpecificationInvoked = false;
                            this.ResumeScheduling();
                        }
                        else
                        {
                            monitor.MonitorEvent(e, senderName, senderType, senderStateName);
                        }
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Assert(bool predicate)
        {
            if (!predicate)
            {
                string msg = "Detected an assertion failure.";
                if (this.SchedulingPolicy is SchedulingPolicy.None)
                {
                    throw new AssertionFailureException(msg);
                }

                this.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
        public void Assert(bool predicate, string s, object arg0)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString());
                if (this.SchedulingPolicy is SchedulingPolicy.None)
                {
                    throw new AssertionFailureException(msg);
                }

                this.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
        public void Assert(bool predicate, string s, object arg0, object arg1)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString(), arg1?.ToString());
                if (this.SchedulingPolicy is SchedulingPolicy.None)
                {
                    throw new AssertionFailureException(msg);
                }

                this.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
        public void Assert(bool predicate, string s, object arg0, object arg1, object arg2)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, arg0?.ToString(), arg1?.ToString(), arg2?.ToString());
                if (this.SchedulingPolicy is SchedulingPolicy.None)
                {
                    throw new AssertionFailureException(msg);
                }

                this.NotifyAssertionFailure(msg);
            }
        }

        /// <inheritdoc/>
        public void Assert(bool predicate, string s, params object[] args)
        {
            if (!predicate)
            {
                var msg = string.Format(CultureInfo.InvariantCulture, s, args);
                if (this.SchedulingPolicy is SchedulingPolicy.None)
                {
                    throw new AssertionFailureException(msg);
                }

                this.NotifyAssertionFailure(msg);
            }
        }

        /// <summary>
        /// Creates a liveness monitor that checks if the specified task eventually completes execution successfully.
        /// </summary>
        internal void MonitorTaskCompletion(Task task)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                task.Status != TaskStatus.RanToCompletion)
            {
                var monitor = new TaskLivenessMonitor(task);
                this.TaskLivenessMonitors.Add(monitor);
            }
        }

        /// <summary>
        /// Starts running a background monitor that checks for potential deadlocks.
        /// </summary>
        private void StartMonitoringDeadlocks() => Task.Factory.StartNew(this.CheckIfExecutionHasDeadlockedAsync,
            this.CancellationSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>
        /// Returns true if the specified thread is controlled, else false.
        /// </summary>
        private bool IsThreadControlled(Thread thread)
        {
            string name = thread?.Name;
            return name != null && this.ControlledThreads.ContainsKey(name);
        }

        /// <summary>
        /// Returns true if the specified task is uncontrolled, else false.
        /// </summary>
        internal bool IsTaskUncontrolled(Task task) => this.IsTaskUncontrolled(task, out _);

        /// <summary>
        /// Returns true if the specified task is uncontrolled, else false.
        /// </summary>
        internal bool IsTaskUncontrolled(Task task, out string methodName)
        {
            if (task is null || task.IsCompleted)
            {
                methodName = null;
                return false;
            }

            return this.UncontrolledTasks.TryGetValue(task, out methodName) ||
                !this.ControlledTasks.ContainsKey(task);
        }

        /// <summary>
        /// Checks if the awaited thread is uncontrolled.
        /// </summary>
        internal bool CheckIfAwaitedThreadIsUncontrolled(Thread thread)
        {
            if (!this.IsThreadControlled(thread))
            {
                this.NotifyUncontrolledThreadWait(thread);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the awaited task is uncontrolled.
        /// </summary>
        internal bool CheckIfAwaitedTaskIsUncontrolled(Task task)
        {
            if (this.IsTaskUncontrolled(task, out string methodName))
            {
                if (string.IsNullOrEmpty(methodName))
                {
                    this.NotifyUncontrolledTaskWait(task);
                }
                else
                {
                    this.NotifyUncontrolledTaskWait(task, methodName);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the task returned from the specified method is uncontrolled.
        /// </summary>
        internal bool CheckIfReturnedTaskIsUncontrolled(Task task, string methodName)
        {
            if (this.IsTaskUncontrolled(task))
            {
                this.NotifyUncontrolledTaskReturned(task, methodName);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the execution has deadlocked. This happens when there are no more enabled operations,
        /// but there is one or more paused operations that are waiting some resource to complete.
        /// </summary>
        private void CheckIfExecutionHasDeadlocked(IReadOnlyList<ControlledOperation> ops)
        {
            if (this.ExecutionStatus != ExecutionStatus.Running || IsAnyOperationEnabled(ops))
            {
                // Either the runtime has stopped executing, or there are still enabled operations, so do not check for a deadlock.
                return;
            }

            // OperationStatus.PausedOnDelay and OperationStatus.PausedOnResourceOrDelay are deliberately
            // ABSENT from every list below, and must stay absent. Both carry a delay that
            // TryEnableOperation decrements each step and that self-enables once it elapses or once nothing
            // else can run, so such an operation is never deadlocked — it is waiting for a timeout that is
            // guaranteed to fire. Adding either here would report a bug on a program that, in reality,
            // simply times out and carries on.
            var pausedOperations = ops.Where(op => op.Status is OperationStatus.Paused).ToList();
            var pausedOnResources = ops.Where(op =>
                op.Status is OperationStatus.PausedOnAnyResource ||
                op.Status is OperationStatus.PausedOnAllResources).ToList();
            var pausedOnReceiveOperations = ops.Where(op => op.Status is OperationStatus.PausedOnReceive).ToList();

            var totalCount = pausedOperations.Count + pausedOnResources.Count + pausedOnReceiveOperations.Count;
            if (totalCount is 0)
            {
                // There are no paused operations, so the execution is not deadlocked.
                return;
            }

            // To simplify the error message, remove the root operation, unless it is the only one that is paused.
            if (totalCount > 1)
            {
                pausedOperations.RemoveAll(op => op.IsRoot);
                pausedOnResources.RemoveAll(op => op.IsRoot);
                pausedOnReceiveOperations.RemoveAll(op => op.IsRoot);
            }

            StringBuilder msg;
            if (this.IsUncontrolledConcurrencyDetected)
            {
                msg = new StringBuilder("Potential deadlock detected.");
            }
            else
            {
                msg = new StringBuilder("Deadlock detected.");
            }

            if (pausedOperations.Count > 0)
            {
                for (int idx = 0; idx < pausedOperations.Count; ++idx)
                {
                    msg.Append(string.Format(CultureInfo.InvariantCulture, " {0}", pausedOperations[idx].Name));
                    if (idx == pausedOperations.Count - 2)
                    {
                        msg.Append(" and");
                    }
                    else if (idx < pausedOperations.Count - 1)
                    {
                        msg.Append(',');
                    }
                }

                msg.Append(pausedOperations.Count is 1 ? " is " : " are ");
                msg.Append("paused on a dependency, but no other controlled operations are enabled.");
            }

            if (pausedOnResources.Count > 0)
            {
                for (int idx = 0; idx < pausedOnResources.Count; ++idx)
                {
                    msg.Append(string.Format(CultureInfo.InvariantCulture, " {0}", pausedOnResources[idx].Name));
                    if (idx == pausedOnResources.Count - 2)
                    {
                        msg.Append(" and");
                    }
                    else if (idx < pausedOnResources.Count - 1)
                    {
                        msg.Append(',');
                    }
                }

                msg.Append(pausedOnResources.Count is 1 ? " is " : " are ");
                msg.Append("waiting to acquire a resource that is already acquired, ");
                msg.Append("but no other controlled operations are enabled.");
            }

            if (pausedOnReceiveOperations.Count > 0)
            {
                for (int idx = 0; idx < pausedOnReceiveOperations.Count; ++idx)
                {
                    msg.Append(string.Format(CultureInfo.InvariantCulture, " {0}", pausedOnReceiveOperations[idx].Name));
                    if (idx == pausedOnReceiveOperations.Count - 2)
                    {
                        msg.Append(" and");
                    }
                    else if (idx < pausedOnReceiveOperations.Count - 1)
                    {
                        msg.Append(',');
                    }
                }

                msg.Append(pausedOnReceiveOperations.Count is 1 ? " is " : " are ");
                msg.Append("waiting to receive an event, but no other controlled operations are enabled.");
            }

            if (this.IsUncontrolledConcurrencyDetected)
            {
                msg.Append(" Due to the presence of uncontrolled concurrency in the test, ");
                msg.Append("Coyote cannot accurately determine if this is a real deadlock or not.");
                if (!this.Configuration.ReportPotentialDeadlocksAsBugs)
                {
                    this.LogWriter.LogInfo("[coyote::test] {0}", msg);
                    this.Detach(ExecutionStatus.Deadlocked);
                }

                msg.Append(" If you believe that this is not a real deadlock, you can disable reporting ");
                msg.Append("potential deadlocks as bugs by setting '--skip-potential-deadlocks' or ");
                msg.Append("'Configuration.WithPotentialDeadlocksReportedAsBugs(false)'.");
            }

            this.NotifyAssertionFailure(msg.ToString());
        }

        /// <summary>
        /// Periodically checks if the execution has deadlocked.
        /// </summary>
        private async Task CheckIfExecutionHasDeadlockedAsync()
        {
            var info = new SchedulingActivityInfo();
            this.LogWriter.LogDebug("[coyote::debug] Started periodic monitoring for potential deadlocks in runtime '{0}'.", this.Id);
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(this.Configuration.DeadlockTimeout), this.CancellationSource.Token);
                    using (SynchronizedSection.Enter(this.RuntimeLock))
                    {
                        if (this.ExecutionStatus != ExecutionStatus.Running)
                        {
                            break;
                        }

                        if (info.OperationCount == this.OperationMap.Count &&
                            info.StepCount == this.Scheduler.StepCount)
                        {
                            string msg = "Potential deadlock or hang detected. The periodic deadlock detection monitor was used, so " +
                                "Coyote cannot accurately determine if this is a deadlock, hang or false positive. If you believe " +
                                "that this is a false positive, you can try increase the deadlock detection timeout by setting " +
                                "'--deadlock-timeout N' or 'Configuration.WithDeadlockTimeout(N)'.";
                            if (Debugger.IsAttached)
                            {
                                msg += " The deadlock or hang was detected with a debugger attached, so Coyote is only inserting " +
                                    "a breakpoint, instead of failing this execution.";
                                this.LogWriter.LogError("[coyote::error] {0}", msg);
                                Debugger.Break();
                            }
                            else if (this.Configuration.ReportPotentialDeadlocksAsBugs)
                            {
                                msg += " Alternatively, you can disable reporting potential deadlocks or hangs as bugs by setting " +
                                    "'--skip-potential-deadlocks' or 'Configuration.WithPotentialDeadlocksReportedAsBugs(false)'.";
                                this.NotifyAssertionFailure(msg);
                            }
                            else
                            {
                                this.LogWriter.LogError("[coyote::error] {0}", msg);
                                this.Detach(ExecutionStatus.Deadlocked);
                            }
                        }
                        else
                        {
                            // Passed check, so continue with the next timeout period.
                            this.LogWriter.LogDebug("[coyote::debug] Passed periodic check for potential deadlocks and hangs in runtime '{0}'.",
                                this.Id);
                            info.OperationCount = this.OperationMap.Count;
                            info.StepCount = this.Scheduler.StepCount;
                            if (this.LastPostponedSchedulingPoint is SchedulingPointType.Pause ||
                                this.LastPostponedSchedulingPoint is SchedulingPointType.Complete)
                            {
                                // A scheduling point was postponed due to a potential deadlock, so try to check if it has been resolved.
                                this.ScheduleNextOperation(default, this.LastPostponedSchedulingPoint.Value, isSuppressible: false);
                            }
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Checks for liveness errors.
        /// </summary>
        internal void CheckLivenessErrors()
        {
            foreach (var monitor in this.TaskLivenessMonitors)
            {
                if (!monitor.IsSatisfied)
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "Found liveness bug at the end of program execution.\nThe stack trace is:\n{0}",
                        FormatSpecificationMonitorStackTrace(monitor.StackTrace));
                    this.NotifyAssertionFailure(msg);
                }
            }

            // Checks if there is a specification monitor stuck in a hot state.
            foreach (var monitor in this.SpecificationMonitors)
            {
                if (monitor.IsInHotState(out string stateName))
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "{0} detected liveness bug in hot state '{1}' at the end of program execution.",
                        monitor.GetType().FullName, stateName);
                    this.NotifyAssertionFailure(msg);
                }
            }
        }

        /// <summary>
        /// Checks if a liveness monitor exceeded its threshold and, if yes, it reports an error.
        /// </summary>
        internal void CheckLivenessThresholdExceeded()
        {
            foreach (var monitor in this.TaskLivenessMonitors)
            {
                if (monitor.IsLivenessThresholdExceeded(this.Configuration.LivenessTemperatureThreshold))
                {
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "Found potential liveness bug at the end of program execution.\nThe stack trace is:\n{0}",
                        FormatSpecificationMonitorStackTrace(monitor.StackTrace));
                    this.NotifyAssertionFailure(msg);
                }
            }

            foreach (var monitor in this.SpecificationMonitors)
            {
                if (monitor.IsLivenessThresholdExceeded(this.Configuration.LivenessTemperatureThreshold))
                {
                    string msg = $"{monitor.Name} detected potential liveness bug in hot state '{monitor.CurrentStateName}'.";
                    this.NotifyAssertionFailure(msg);
                }
            }
        }

        /// <summary>
        /// Checks if the scheduling steps bound has been reached. If yes,
        /// it stops the scheduler and kills all enabled machines.
        /// </summary>
        private void CheckIfSchedulingStepsBoundIsReached()
        {
            if (this.Scheduler.IsMaxStepsReached)
            {
                string message = $"Scheduling steps bound of {this.Scheduler.StepCount} reached.";
                if (this.Configuration.FailOnMaxStepsBound)
                {
                    this.NotifyAssertionFailure(message);
                }
                else
                {
                    this.LogWriter.LogDebug("[coyote::debug] {0}", message);
                    this.Detach(ExecutionStatus.BoundReached);
                }
            }
        }

        /// <summary>
        /// Notify that an exception was not handled.
        /// </summary>
        internal void NotifyUnhandledException(Exception ex, string message)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus != ExecutionStatus.Running)
                {
                    return;
                }

                if (this.UnhandledException is null)
                {
                    this.UnhandledException = ex;
                }

                this.NotifyAssertionFailure(message);
            }
        }

        /// <summary>
        /// Notify that an assertion has failed.
        /// </summary>
        internal void NotifyAssertionFailure(string text)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.ExecutionStatus is ExecutionStatus.Running)
                {
                    this.BugReport = text;
                    this.LogManager.LogAssertionFailure($"[coyote::error] {text}");
                    this.RaiseOnFailureEvent(new AssertionFailureException(text));
                    if (Debugger.IsAttached)
                    {
                        Debugger.Break();
                    }

                    this.Detach(ExecutionStatus.BugFound);
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled method invocation was detected.
        /// </summary>
        internal void NotifyUncontrolledInvocation(string methodName)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy != SchedulingPolicy.None)
                {
                    this.UncontrolledInvocations.Add(methodName);
                }

                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Invoking '{methodName}' is not intercepted and controlled during " +
                        "testing, so it can interfere with the ability to reproduce bug traces.";
                    this.TryHandleUncontrolledConcurrency(message, methodName);
                }
            }
        }

        /// <summary>
        /// Notify that a primitive the runtime can normally control was created in a shape it does not
        /// implement, so the program keeps the real, uncontrolled one.
        /// </summary>
        /// <remarks>
        /// Unlike the other notifications here this neither detaches nor fails the test. The primitive
        /// behaves correctly; it is simply invisible to the scheduler, so the only cost is the
        /// interleavings that are never explored — which a green run otherwise looks exactly like.
        /// Warned once per description, because these are routinely created in a loop, and recorded as
        /// an uncontrolled invocation so that it also survives into the test report.
        /// </remarks>
        internal void NotifyUncontrolledPrimitive(string description)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving &&
                    this.UncontrolledInvocations.Add(description))
                {
                    this.LogWriter.LogWarning("[coyote::warning] {0} is not controlled during testing, " +
                        "so interleavings that depend on it are not explored.", description);
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled synchronization method invocation was detected.
        /// </summary>
        internal void NotifyUncontrolledSynchronizationInvocation(string methodName)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Executing thread '{Thread.CurrentThread.ManagedThreadId}' is not controlled and " +
                        $"is invoking the {methodName} synchronization method, which can cause deadlocks during testing.";
                    if (this.Configuration.IsPartiallyControlledConcurrencyAllowed ||
                        this.Configuration.IsSystematicFuzzingFallbackEnabled)
                    {
                        // An uncontrolled thread (e.g. a Timer callback) is completing/releasing a controlled
                        // synchronization primitive. The caller performs the mutation under the runtime lock, so it
                        // stays atomic with respect to the scheduler; a paused controlled operation awaiting it is
                        // re-enabled by the periodic deadlock monitor. Tolerate it exactly as the other
                        // uncontrolled-concurrency notifications do (see TryHandleUncontrolledConcurrency).
                        this.LogWriter.LogWarning("[coyote::warning] {0}", message);
                        this.IsUncontrolledConcurrencyDetected = true;
                        if (this.Configuration.IsPartiallyControlledConcurrencyAllowed)
                        {
                            // Stay attached to the controlled scheduler and let the caller finish the operation.
                            return;
                        }

                        this.Detach(ExecutionStatus.ConcurrencyUncontrolled);
                    }
                    else
                    {
                        this.NotifyAssertionFailure(message);
                    }
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled data non-deterministic method invocation was detected.
        /// </summary>
        internal void NotifyUncontrolledDataNondeterministicInvocation(string methodName)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy != SchedulingPolicy.None)
                {
                    this.UncontrolledInvocations.Add(methodName);
                }

                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Invoking '{methodName}' introduces data non-determinism that is not intercepted " +
                        "and controlled during testing, so it can interfere with the ability to reproduce bug traces.";
                    if (this.Configuration.IsPartiallyControlledDataNondeterminismAllowed ||
                        this.Configuration.IsSystematicFuzzingFallbackEnabled)
                    {
                        if (this.Configuration.IsUncontrolledInvocationStackTraceLoggingEnabled)
                        {
                            this.LogWriter.LogWarning("[coyote::warning] {0}{1}{2}", message, Environment.NewLine,
                                FormatUncontrolledStackTrace(new StackTrace()));
                        }
                        else
                        {
                            this.LogWriter.LogWarning("[coyote::warning] {0}", message);
                        }
                    }
                    else
                    {
                        this.NotifyAssertionFailure(FormatUncontrolledInvocationExceptionMessage(message, methodName));
                    }
                }
            }
        }

        /// <summary>
        /// Notify that the specified executing thread is uncontrolled.
        /// </summary>
        private void NotifyUncontrolledThreadExecution(Thread thread)
        {
            if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
            {
                // TODO: figure out if there is a way to get more information about the creator of the
                // uncontrolled thread to ease the user debugging experience.
                string message = $"Executing thread '{thread.ManagedThreadId}' is not intercepted and controlled " +
                    "during testing, so it can interfere with the ability to reproduce bug traces.";
                if (this.TryHandleUncontrolledConcurrency(message) && thread != Thread.CurrentThread)
                {
                    this.TryPauseAndResolveUncontrolledCondition(() => thread.Join(0));
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled thread is being waited.
        /// </summary>
        private void NotifyUncontrolledThreadWait(Thread thread)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Waiting thread '{thread.ManagedThreadId}' that is not intercepted and controlled " +
                        "during testing, so it can interfere with the ability to reproduce bug traces.";
                    if (this.TryHandleUncontrolledConcurrency(message) && thread != Thread.CurrentThread)
                    {
                        this.TryPauseAndResolveUncontrolledCondition(() => thread.Join(0));
                    }
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled task is being waited.
        /// </summary>
        private void NotifyUncontrolledTaskWait(Task task)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Waiting task '{task.Id}' that is not intercepted and controlled during " +
                        "testing, so it can interfere with the ability to reproduce bug traces.";
                    if (this.TryHandleUncontrolledConcurrency(message))
                    {
                        this.UncontrolledTasks.TryAdd(task, null);
                        this.TryPauseAndResolveUncontrolledCondition(() => task.IsCompleted);
                    }
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled task with a known source is being waited.
        /// </summary>
        private void NotifyUncontrolledTaskWait(Task task, string methodName)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Waiting task '{task.Id}' from '{methodName}' that is not intercepted and controlled " +
                        "during testing, so it can interfere with the ability to reproduce bug traces.";
                    if (this.TryHandleUncontrolledConcurrency(message, methodName))
                    {
                        this.UncontrolledTasks.TryAdd(task, methodName);
                        this.TryPauseAndResolveUncontrolledCondition(() => task.IsCompleted);
                    }
                }
            }
        }

        /// <summary>
        /// Notify that an uncontrolled task was returned.
        /// </summary>
        private void NotifyUncontrolledTaskReturned(Task task, string methodName)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                if (this.SchedulingPolicy != SchedulingPolicy.None)
                {
                    this.UncontrolledInvocations.Add(methodName);
                }

                if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                {
                    string message = $"Invoking '{methodName}' returned task '{task.Id}' that is not intercepted and " +
                        "controlled during testing, so it can interfere with the ability to reproduce bug traces.";
                    if (this.TryHandleUncontrolledConcurrency(message, methodName))
                    {
                        this.UncontrolledTasks.TryAdd(task, methodName);
                        this.TryPauseAndResolveUncontrolledCondition(() => task.IsCompleted);
                    }
                }
            }
        }

        /// <summary>
        /// Invoked when uncontrolled concurrency is detected. Based on the test configuration, it can try
        /// handle the uncontrolled concurrency, else it terminates the current test iteration.
        /// </summary>
        private bool TryHandleUncontrolledConcurrency(string message, string methodName = default)
        {
            if (this.Configuration.IsPartiallyControlledConcurrencyAllowed ||
                this.Configuration.IsSystematicFuzzingFallbackEnabled)
            {
                if (this.Configuration.IsUncontrolledInvocationStackTraceLoggingEnabled)
                {
                    this.LogWriter.LogWarning("[coyote::warning] {0}{1}{2}", message, Environment.NewLine,
                        FormatUncontrolledStackTrace(new StackTrace()));
                }
                else
                {
                    this.LogWriter.LogWarning("[coyote::warning] {0}", message);
                }

                this.IsUncontrolledConcurrencyDetected = true;
                if (this.Configuration.IsPartiallyControlledConcurrencyAllowed)
                {
                    return true;
                }

                this.Detach(ExecutionStatus.ConcurrencyUncontrolled);
            }
            else
            {
                this.NotifyAssertionFailure(FormatUncontrolledInvocationExceptionMessage(message, methodName));
            }

            return false;
        }

        /// <summary>
        /// Throws an <see cref="AssertionFailureException"/> exception containing the specified exception.
        /// </summary>
        internal void WrapAndThrowException(Exception exception, string s, params object[] args)
        {
            string msg = string.Format(CultureInfo.InvariantCulture, s, args);
            string message = string.Format(CultureInfo.InvariantCulture,
                "Exception '{0}' was thrown in {1}: {2}\n" +
                "from location '{3}':\n" +
                "The stack trace is:\n{4}",
                exception.GetType(), msg, exception.Message, exception.Source, exception.StackTrace);

            if (this.SchedulingPolicy is SchedulingPolicy.None)
            {
                throw new AssertionFailureException(message, exception);
            }

            this.NotifyUnhandledException(exception, message);
        }

        /// <summary>
        /// Formats the message of the uncontrolled invocation exception.
        /// </summary>
        private static string FormatUncontrolledInvocationExceptionMessage(string message, string methodName = default)
        {
            string trace = FormatUncontrolledStackTrace(new StackTrace());
            var mockMessage = methodName is null ? string.Empty : $" either replace or mock '{methodName}', or";
            return $"{message} As a workaround, you can{mockMessage} use the '--no-repro' command line option " +
                "(or the 'Configuration.WithNoBugTraceRepro()' method) to ignore this error by disabling bug " +
                $"trace repro. Learn more at https://aka.ms/coyote-no-repro.{Environment.NewLine}{trace}";
        }

        /// <summary>
        /// Processes an unhandled exception in the specified controlled operation.
        /// </summary>
        internal void ProcessUnhandledExceptionInOperation(ControlledOperation op, Exception exception)
        {
            // Complete the failed operation. This is required so that the operation does not throw if it detaches.
            op.Status = OperationStatus.Completed;

            if (exception is AggregateException aex)
            {
                exception = aex.Flatten().InnerExceptions.OfType<ThreadInterruptedException>().FirstOrDefault() ?? exception;
            }

            // Ignore this exception, its thrown by the runtime to terminate controlled threads.
            if (!(exception is ThreadInterruptedException || exception.GetBaseException() is ThreadInterruptedException))
            {
                // Report the unhandled exception.
                string trace = FormatExceptionStackTrace(exception);
                string message = $"Unhandled exception. {trace}";
                this.NotifyUnhandledException(exception, message);
            }
        }

        /// <summary>
        /// Formats the stack trace of the specified exception.
        /// </summary>
        private static string FormatExceptionStackTrace(Exception exception)
        {
#if NET
            string[] lines = exception.ToString().Split(Environment.NewLine, StringSplitOptions.None);
#else
            string[] lines = exception.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
#endif
            for (int i = 0; i < lines.Length; ++i)
            {
                if (lines[i].StartsWith("   at Microsoft.Coyote.Rewriting", StringComparison.Ordinal))
                {
                    lines[i] = string.Empty;
                }
            }

            return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrEmpty(line)));
        }

        /// <summary>
        /// Formats the specified stack trace of an uncontrolled invocation.
        /// </summary>
        private static string FormatUncontrolledStackTrace(StackTrace trace)
        {
            StringBuilder sb = new StringBuilder();
#if NET
            string[] lines = trace.ToString().Split(Environment.NewLine, StringSplitOptions.None);
#else
            string[] lines = trace.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
#endif
            foreach (var line in lines.Where(line => !line.Contains("at Microsoft.Coyote")))
            {
                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Formats the specified stack trace of a specification monitor.
        /// </summary>
        private static string FormatSpecificationMonitorStackTrace(StackTrace trace)
        {
            StringBuilder sb = new StringBuilder();
#if NET
            string[] lines = trace.ToString().Split(Environment.NewLine, StringSplitOptions.None);
#else
            string[] lines = trace.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
#endif
            foreach (var line in lines)
            {
                if ((line.Contains("at Microsoft.Coyote.Specifications") ||
                    line.Contains("at Microsoft.Coyote.Runtime")) &&
                    !line.Contains($"at {typeof(Specification).FullName}.{nameof(Specification.Monitor)}"))
                {
                    continue;
                }

                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Raises the <see cref="OnFailure"/> event with the specified <see cref="Exception"/>.
        /// </summary>
        internal void RaiseOnFailureEvent(Exception exception)
        {
            if (Debugger.IsAttached)
            {
                Debugger.Break();
            }

            this.OnFailure?.Invoke(exception);
        }

        /// <summary>
        /// Populates the specified test report.
        /// </summary>
        internal void PopulateTestReport(ITestReport report)
        {
            using (SynchronizedSection.Enter(this.RuntimeLock))
            {
                bool isBugFound = this.ExecutionStatus is ExecutionStatus.BugFound;
                int groupingDegree = this.OperationMap.Values.Select(op => op.Group).Distinct().Count();
                report.SetSchedulingStatistics(isBugFound, this.BugReport, this.OperationMap.Count, (int)this.MaxConcurrencyDegree,
                    groupingDegree, this.Scheduler.StepCount, this.Scheduler.IsMaxStepsReached, this.Scheduler.IsIterationFair);
                if (isBugFound)
                {
                    report.SetUnhandledException(this.UnhandledException);
                }

                report.SetUncontrolledInvocations(this.UncontrolledInvocations);
            }
        }

        /// <summary>
        /// Builds the <see cref="CoverageInfo"/>.
        /// </summary>
        internal CoverageInfo BuildCoverageInfo() => this.Extension.BuildCoverageInfo() ?? this.CoverageInfo;

        /// <summary>
        /// Returns the <see cref="CoverageGraph"/> of the current execution.
        /// </summary>
        internal CoverageGraph GetCoverageGraph() => this.Extension.GetCoverageGraph();

        /// <summary>
        /// Enters the synchronized section of the runtime. When the synchronized section
        /// gets disposed, the thread will automatically exit it.
        /// </summary>
        internal SynchronizedSection EnterSynchronizedSection() => SynchronizedSection.Enter(this.RuntimeLock);

        /// <summary>
        /// Sets up the context of the executing controlled thread, allowing future retrieval
        /// of runtime related data from the same thread, as well as across threads that share
        /// the same asynchronous control flow.
        /// </summary>
        private void SetCurrentExecutionContext(ControlledOperation op)
        {
            AsyncLocalRuntime.Value = this;
            ThreadLocalRuntime = this;
            ExecutingOperation = op;
            SynchronizationContext.SetSynchronizationContext(this.SyncContext);
        }

        /// <summary>
        /// Handlers that clear per-thread state owned by assemblies that this one cannot reference.
        /// </summary>
        /// <remarks>
        /// <see cref="CleanCurrentExecutionContext"/> cannot reach thread-static state declared in
        /// 'Microsoft.Coyote.Test', so that assembly registers a handler here instead.
        /// </remarks>
        private static Action ThreadStateResetHandlers;

        /// <summary>
        /// Registers a handler that clears per-thread state when a controlled thread finishes
        /// executing an operation. Registering the same handler more than once has no effect.
        /// </summary>
        internal static void RegisterThreadStateResetHandler(Action handler)
        {
            if (handler != null)
            {
                // Delegate equality compares target and method, so re-registering the same static
                // method is a no-op. This matters because the registering type is set up per test.
                ThreadStateResetHandlers = (ThreadStateResetHandlers?.GetInvocationList().Contains(handler) ?? false) ?
                    ThreadStateResetHandlers : ThreadStateResetHandlers + handler;
            }
        }

        /// <summary>
        /// Removes any runtime related data from the context of the executing controlled thread.
        /// </summary>
        /// <remarks>
        /// Everything set up by <see cref="SetCurrentExecutionContext"/>, plus any other per-thread
        /// state the runtime installs, must be cleared here. A controlled thread can outlive the
        /// operation it was executing, so state left behind can be observed by a subsequent operation
        /// or can keep a disposed runtime alive.
        /// </remarks>
        private static void CleanCurrentExecutionContext()
        {
            ExecutingOperation = null;
            ThreadLocalRuntime = null;
            AsyncLocalRuntime.Value = null;

            // Set by SetCurrentExecutionContext but, until now, never cleared. Leaving it installed
            // means the thread holds a synchronization context whose runtime is about to be disposed,
            // and whose Post then silently drops continuations.
            SynchronizationContext.SetSynchronizationContext(null);

            // Stored in an async local that is otherwise never reset.
            FuzzingStrategy.ClearOperationId();

            ThreadStateResetHandlers?.Invoke();
        }

        /// <inheritdoc/>
        public void RegisterLog(IRuntimeLog log) => this.LogManager.RegisterLog(log, this.LogWriter);

        /// <inheritdoc/>
        public void RemoveLog(IRuntimeLog log) => this.LogManager.RemoveLog(log);

        /// <inheritdoc/>
        public void Stop() => this.IsRunning = false;

        /// <summary>
        /// Detaches the scheduler and interrupts all controlled operations.
        /// </summary>
        /// <remarks>
        /// It is assumed that this method runs in the scope of a <see cref="SynchronizedSection"/>.
        /// </remarks>
        private void Detach(ExecutionStatus status)
        {
            if (this.ExecutionStatus != ExecutionStatus.Running)
            {
                return;
            }

            try
            {
                if (status is ExecutionStatus.PathExplored)
                {
                    this.LogWriter.LogInfo("[coyote::test] Exploration finished in runtime '{0}' [reached the end of the test method].", this.Id);
                }
                else if (status is ExecutionStatus.BoundReached)
                {
                    this.LogWriter.LogInfo("[coyote::test] Exploration finished in runtime '{0}' [reached the given bound].", this.Id);
                }
                else if (status is ExecutionStatus.Deadlocked)
                {
                    this.LogWriter.LogInfo("[coyote::test] Exploration finished in runtime '{0}' [detected a potential deadlock].", this.Id);
                }
                else if (status is ExecutionStatus.ConcurrencyUncontrolled)
                {
                    this.LogWriter.LogInfo("[coyote::test] Exploration finished in runtime '{0}' [detected uncontrolled concurrency].", this.Id);
                }
                else if (status is ExecutionStatus.BugFound)
                {
                    this.LogWriter.LogInfo("[coyote::test] Exploration finished in runtime '{0}' [found a bug using the '{1}' strategy].",
                        this.Id, this.Scheduler.GetStrategyName());
                }

                // Register the explored execution path for coverage.
                this.CoverageInfo.DeclareExploredExecutionPath(this.Scheduler.Trace.GetDigest());

                this.ExecutionStatus = status;
                this.CancellationSource.Cancel();

                // Complete any remaining operations at the end of the schedule.
                ControlledOperation current = ExecutingOperation;
                foreach (var op in this.OperationMap.Values)
                {
                    if (op.Status != OperationStatus.Completed && op != current)
                    {
                        // Force the operation to complete and interrupt its thread.
                        op.Status = OperationStatus.Completed;
                        if (this.ThreadPool.TryGetValue(op.Id, out Thread thread))
                        {
                            thread.Interrupt();
                        }
                    }
                }

                // Nothing is schedulable once the execution has detached. This must iterate the
                // operation map above rather than the schedulable operations, because an operation
                // that completed without being swept yet still needs its thread interrupted.
                this.SchedulableOperations.Clear();

                if (current != null && current.Status != OperationStatus.Completed)
                {
                    // Force the current operation to complete and interrupt the current thread.
                    current.Status = OperationStatus.Completed;
                    throw new ThreadInterruptedException();
                }
            }
            finally
            {
                // Check if the completion source is completed, else set its result.
                if (!this.CompletionSource.Task.IsCompleted)
                {
                    this.IsRunning = false;
                    this.CompletionSource.SetResult(true);
                }
            }
        }

        /// <summary>
        /// Disposes runtime resources.
        /// </summary>
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                RuntimeProvider.Deregister(this.Id);
                using (SynchronizedSection.Enter(this.RuntimeLock))
                {
                    foreach (var op in this.OperationMap.Values)
                    {
                        op.Dispose();
                    }

                    foreach (var handler in this.PendingStartOperationMap.Values)
                    {
                        handler.Dispose();
                    }

                    this.ThreadPool.Clear();
                    this.OperationMap.Clear();
                    this.SchedulableOperations.Clear();
                    this.PendingStartOperationMap.Clear();
                    this.ControlledThreads.Clear();
                    this.ControlledTasks.Clear();
                    this.UncontrolledTasks.Clear();
                    this.UncontrolledInvocations.Clear();
                    this.SpecificationMonitors.Clear();
                    this.TaskLivenessMonitors.Clear();
                    this.StateHashingFunctions.Clear();

                    if (!(this.Extension is NullRuntimeExtension))
                    {
                        this.Extension.Dispose();
                    }

                    this.ControlledTaskScheduler.Dispose();
                    this.SyncContext.Dispose();
                    this.CancellationSource.Dispose();
                    this.LogWriter.Dispose();
                }

                if (this.SchedulingPolicy != SchedulingPolicy.None)
                {
                    Interlocked.Decrement(ref ModelledRuntimeUseCount);
                    if (this.SchedulingPolicy is SchedulingPolicy.Interleaving)
                    {
                        // Note: this makes it possible to run a Controlled unit test followed by a production
                        // unit test, whereas before that would throw "Uncontrolled Task" exceptions.
                        // This does not solve mixing unit test type in parallel.
                        Interlocked.Decrement(ref ExecutionControlledUseCount);
                    }
                }
            }
        }

        /// <summary>
        /// Disposes runtime resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
