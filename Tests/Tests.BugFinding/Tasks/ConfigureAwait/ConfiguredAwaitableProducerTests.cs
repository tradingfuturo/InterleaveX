// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NET
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.SystematicTesting;
using Xunit;
using Xunit.Abstractions;
using CoyoteCompiler = Microsoft.Coyote.Rewriting.Types.Runtime.CompilerServices;

namespace Microsoft.Coyote.BugFinding.Tests
{
    /// <summary>
    /// Tests the shapes that produce a configured awaitable from a type the rewriter does not own.
    /// </summary>
    /// <remarks>
    /// The rewriter retypes every local, field and call that mentions a known compiler type such as
    /// <c>ConfiguredValueTaskAwaitable</c>. That is only sound while whatever produced the value was
    /// rewritten as well: a real awaitable reaching a call site expecting the controlled one is a
    /// struct of a different shape, and reinterpreting it corrupts memory rather than failing
    /// cleanly (it surfaced as an <c>AccessViolationException</c> that took down the whole test host,
    /// so a plain assertion on behavior is enough — these tests could not even run before the fix).
    /// <para>
    /// Each test below drives one such producer end to end. The companion assertions on the awaitable
    /// types pin down that the producer really was redirected, so that this keeps testing what it is
    /// meant to if a future rewriter change silently stops rewriting one of them.
    /// </para>
    /// </remarks>
    public class ConfiguredAwaitableProducerTests : BaseBugFindingTest
    {
        public ConfiguredAwaitableProducerTests(ITestOutputHelper output)
            : base(output)
        {
        }

        /// <summary>
        /// A hand-rolled async enumerable, so that the enumeration under test is not itself a
        /// compiler-generated async iterator.
        /// </summary>
        private class Sequence : IAsyncEnumerable<int>
        {
            private readonly int Count;

            internal Sequence(int count)
            {
                this.Count = count;
            }

            internal bool IsDisposed { get; private set; }

            internal CancellationToken ObservedToken { get; private set; }

            public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                this.ObservedToken = cancellationToken;
                return new Enumerator(this);
            }

            private class Enumerator : IAsyncEnumerator<int>
            {
                private readonly Sequence Owner;
                private int Position;

                internal Enumerator(Sequence owner)
                {
                    this.Owner = owner;
                    this.Position = -1;
                }

                public int Current => this.Position;

                public async ValueTask<bool> MoveNextAsync()
                {
                    await Task.Yield();
                    this.Position++;
                    return this.Position < this.Owner.Count;
                }

                public ValueTask DisposeAsync()
                {
                    this.Owner.IsDisposed = true;
                    return default;
                }
            }
        }

        private class AsyncResource : IAsyncDisposable
        {
            internal bool IsDisposed { get; private set; }

            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                this.IsDisposed = true;
            }
        }

        [Fact(Timeout = 10000)]
        public void TestAwaitForeachWithCancellation()
        {
            this.Test(async () =>
            {
                var source = new Sequence(3);
                using var cts = new CancellationTokenSource();

                int sum = 0;
                await foreach (int value in source.WithCancellation(cts.Token))
                {
                    sum += value;
                }

                Specification.Assert(sum is 0 + 1 + 2, "Enumeration summed {0} instead of 3.", sum);
                Specification.Assert(source.ObservedToken == cts.Token,
                    "The token given to 'WithCancellation' did not reach the enumerator.");
                Specification.Assert(source.IsDisposed, "The enumerator was not disposed.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestAwaitForeachConfigureAwait()
        {
            this.Test(async () =>
            {
                var source = new Sequence(3);

                int sum = 0;
                await foreach (int value in source.ConfigureAwait(false))
                {
                    sum += value;
                }

                Specification.Assert(sum is 0 + 1 + 2, "Enumeration summed {0} instead of 3.", sum);
                Specification.Assert(source.IsDisposed, "The enumerator was not disposed.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestAwaitForeachWithCancellationAndConfigureAwait()
        {
            this.Test(async () =>
            {
                var source = new Sequence(3);
                using var cts = new CancellationTokenSource();

                int sum = 0;
                await foreach (int value in source.WithCancellation(cts.Token).ConfigureAwait(false))
                {
                    sum += value;
                }

                Specification.Assert(sum is 0 + 1 + 2, "Enumeration summed {0} instead of 3.", sum);
                Specification.Assert(source.ObservedToken == cts.Token,
                    "The token given to 'WithCancellation' did not survive the 'ConfigureAwait'.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestAwaitUsingConfigureAwait()
        {
            this.Test(async () =>
            {
                var resource = new AsyncResource();
                await using (resource.ConfigureAwait(false))
                {
                }

                Specification.Assert(resource.IsDisposed, "The resource was not disposed.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

#if NET8_0_OR_GREATER
        [Fact(Timeout = 10000)]
        public void TestConfigureAwaitOptions()
        {
            this.Test(async () =>
            {
                var entry = new SharedEntry();
                Task write = Task.Run(async () =>
                {
                    await Task.Yield();
                    entry.Value = 5;
                });

                await write.ConfigureAwait(ConfigureAwaitOptions.None);
                AssertSharedEntryValue(entry, 5);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestGenericConfigureAwaitOptions()
        {
            // A task with a result has the options overload too, and its awaitable is a different
            // generic type, so it needs its own redirection.
            this.Test(async () =>
            {
                Task<int> compute = Task.Run(async () =>
                {
                    await Task.Yield();
                    return 5;
                });

                int result = await compute.ConfigureAwait(ConfigureAwaitOptions.None);
                Specification.Assert(result is 5, "Result is {0} instead of 5.", result);
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestGenericConfigureAwaitOptionsPropagatesFailure()
        {
            this.TestWithError(async () =>
            {
                Task<int> faulted = Task.Run<int>(async () =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("expected");
                });

                await faulted.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            errorChecker: (e) => Assert.Contains("Unhandled exception. System.InvalidOperationException", e),
            replay: true);
        }

        [Fact(Timeout = 10000)]
        public void TestConfigureAwaitOptionsForceYielding()
        {
            // 'ForceYielding' asks for a suspension point even when the task has already completed.
            // Reporting completion instead would resume inline and drop the interleaving between the
            // two operations below, which is exactly the interleaving the bug depends on.
            this.TestWithError(async () =>
            {
                var entry = new SharedEntry();
                Task setter = Task.Run(() =>
                {
                    entry.Value = 3;
                });

                entry.Value = 5;

                // The task is very often already complete here, so without a forced yield the
                // continuation runs inline and the racing write never gets to interleave.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                AssertSharedEntryValue(entry, 5);
                await setter;
            },
            configuration: this.GetConfiguration().WithTestingIterations(500),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 10000)]
        public void TestGenericConfigureAwaitOptionsForceYielding()
        {
            // Mirrors the non-generic test above, and deliberately so. Asserting only that the result
            // survives the await passes just as well when 'ForceYielding' is ignored, because resuming
            // inline returns the same value; the yield is only observable as the interleaving it lets
            // the racing write take.
            this.TestWithError(async () =>
            {
                var entry = new SharedEntry();
                Task setter = Task.Run(() =>
                {
                    entry.Value = 3;
                });

                entry.Value = 5;

                Task<int> completed = Task.FromResult(7);
                int result = await completed.ConfigureAwait(
                    ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);
                Specification.Assert(result is 7, "Result is {0} instead of 7.", result);
                AssertSharedEntryValue(entry, 5);
                await setter;
            },
            configuration: this.GetConfiguration().WithTestingIterations(500),
            expectedError: "Value is 3 instead of 5.",
            replay: true);
        }

        [Fact(Timeout = 20000)]
        public void TestConfigureAwaitPathsAgreeOnCapturedContext()
        {
            // Both overloads force the continuation onto the controlled context while a runtime is
            // testing, so neither may leave the awaiting operation uncontrolled. 'ConfigureAwaitOptions.None'
            // asks for the same thing 'ConfigureAwait(false)' does, and the two must keep answering it the
            // same way: honoring 'None' literally on one path only would not restore the BCL contract, it
            // would leave the continuation controlled on one path and not the other. This pins that they
            // agree, which is the property the two constructors are easy to let drift apart on.
            foreach (bool useOptions in new[] { false, true })
            {
                TestReport report = this.RunSystematicTest(async () =>
                {
                    var entry = new SharedEntry();
                    Task work = Task.Run(async () =>
                    {
                        await Task.Yield();
                        entry.Value = 3;
                    });

                    if (useOptions)
                    {
                        await work.ConfigureAwait(ConfigureAwaitOptions.None);
                    }
                    else
                    {
                        await work.ConfigureAwait(false);
                    }

                    Specification.Assert(entry.Value is 3, "The awaited work should have completed.");
                },
                this.GetConfiguration().WithTestingIterations(100));

                Assert.Empty(report.UncontrolledInvocations);
                Assert.Equal(0, report.NumOfFoundBugs);
            }
        }

        [Fact(Timeout = 10000)]
        public void TestConfigureAwaitOptionsSuppressThrowing()
        {
            // 'SuppressThrowing' is decided by the awaiter the controlled awaitable wraps, so it must
            // survive the redirection: the faulted task is awaited without its exception surfacing.
            this.Test(async () =>
            {
                Task faulted = Task.Run(async () =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("expected");
                });

                await faulted.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Specification.Assert(faulted.IsFaulted, "The awaited task should still be faulted.");
            },
            this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 10000)]
        public void TestConfigureAwaitOptionsPropagatesFailure()
        {
            // Without 'SuppressThrowing' the fault must still propagate, so that the redirection is
            // not quietly swallowing exceptions.
            this.TestWithError(async () =>
            {
                Task faulted = Task.Run(async () =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("expected");
                });

                await faulted.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            errorChecker: (e) => Assert.Contains("Unhandled exception. System.InvalidOperationException", e),
            replay: true);
        }
#endif

        [Fact(Timeout = 5000)]
        public void TestConfiguredAwaitableProducersAreRedirected()
        {
            // The tests above only exercise the redirected producers as long as the rewriter really
            // is redirecting them. Assert that directly, so that losing the redirection shows up here
            // rather than as the tests silently going back to testing the uncontrolled BCL types.
            this.Test(() =>
            {
                var source = new Sequence(1);
                object withCancellation = source.WithCancellation(default);
                Specification.Assert(
                    withCancellation is CoyoteCompiler.ConfiguredCancelableAsyncEnumerable<int>,
                    "'WithCancellation' was not redirected: '{0}'.", withCancellation.GetType().FullName);

                object configured = source.ConfigureAwait(false);
                Specification.Assert(
                    configured is CoyoteCompiler.ConfiguredCancelableAsyncEnumerable<int>,
                    "'ConfigureAwait' was not redirected: '{0}'.", configured.GetType().FullName);

                object enumerator = ((CoyoteCompiler.ConfiguredCancelableAsyncEnumerable<int>)configured)
                    .GetAsyncEnumerator();
                Specification.Assert(
                    enumerator is CoyoteCompiler.ConfiguredCancelableAsyncEnumerable<int>.Enumerator,
                    "'GetAsyncEnumerator' was not redirected: '{0}'.", enumerator.GetType().FullName);

                object disposable = new AsyncResource().ConfigureAwait(false);
                Specification.Assert(disposable is CoyoteCompiler.ConfiguredAsyncDisposable,
                    "The async disposable 'ConfigureAwait' was not redirected: '{0}'.",
                    disposable.GetType().FullName);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestBoxedConfiguredAwaitableCarriesRewrittenType()
        {
            // Boxing is what makes the assertions above meaningful, and it is a type token in its own
            // right: the rewriter has to rewrite it alongside the value, or the box describes the
            // original type while holding the controlled type's storage. That mismatch is silent
            // corruption rather than a failure, so assert the boxed type directly.
            this.Test(() =>
            {
                object configured = Task.CompletedTask.ConfigureAwait(false);
                Specification.Assert(configured is CoyoteCompiler.ConfiguredTaskAwaitable,
                    "A boxed configured awaitable carries the type '{0}' instead of the controlled one.",
                    configured.GetType().FullName);
            });
        }
    }
}
#endif
