// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Specifications;
using Xunit;
using Xunit.Abstractions;
using Monitor = System.Threading.Monitor;

namespace Microsoft.Coyote.BugFinding.Tests
{
    public class LockStatementTests : BaseBugFindingTest
    {
        public LockStatementTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestLockUnlock()
        {
            this.Test(() =>
            {
                int value = 0;
                object sync = new object();
                lock (sync)
                {
                    value++;
                }

                lock (sync)
                {
                    value++;
                }

                int expected = 2;
                Specification.Assert(value == expected, "Value is {0} instead of {1}.", value, expected);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestReentrantLock()
        {
            this.Test(() =>
            {
                int value = 0;
                object sync = new object();
                lock (sync)
                {
                    value++;
                    lock (sync)
                    {
                        value++;
                    }
                }

                int expected = 2;
                Specification.Assert(value == expected, "Value is {0} instead of {1}.", value, expected);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestWaitPulse()
        {
            this.Test(async () =>
            {
                string value = string.Empty;
                object sync = new object();
                var t1 = Task.Run(() =>
                {
                    lock (sync)
                    {
                        if (value != "put")
                        {
                            Monitor.Wait(sync);
                        }

                        value = "taken";
                    }
                });

                var t2 = Task.Run(() =>
                {
                    lock (sync)
                    {
                        value = "put";
                        Monitor.Pulse(sync);
                    }
                });

                await Task.WhenAll(t1, t2);

                var expected = "taken";
                Specification.Assert(value == expected, "Value is {0} instead of {1}.", value, expected);
            });
        }

        [Fact(Timeout = 5000)]
        public void TestMonitorWithLockTaken()
        {
            this.Test(() =>
            {
                object sync = new object();
                bool lockTaken = false;
                Monitor.TryEnter(sync, ref lockTaken);
                if (lockTaken)
                {
                    Monitor.Exit(sync);
                }

                Specification.Assert(lockTaken, "lockTaken is false");
            },
            this.GetConfiguration());
        }

        [Fact(Timeout = 5000)]
        public void TestLockInStaticConstructorDoesNotDeadlock()
        {
            this.Test(() =>
            {
                // Accessing this type triggers its static constructor which contains a lock.
                // Without the fix for issue #488, this would deadlock because the rewriter
                // would replace Monitor.Enter with Coyote's scheduler-aware version inside
                // the .cctor, and the scheduler could suspend the thread while the CLR's
                // type initialization lock is held.
                var instance = new ClassWithLockInStaticConstructor();
                Specification.Assert(ClassWithLockInStaticConstructor.IsInitialized,
                    "Static constructor should have completed.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestSchedulingPointInStaticConstructorDoesNotDeadlock()
        {
            this.Test(() =>
            {
                var instance = new ClassWithSchedulingPointInStaticConstructor();
                Specification.Assert(ClassWithSchedulingPointInStaticConstructor.IsInitialized,
                    "Static constructor should have completed.");
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        [Fact(Timeout = 5000)]
        public void TestConcurrentAccessToTypeWithLockInStaticConstructor()
        {
            this.Test(() =>
            {
                var t = new Thread(() =>
                {
                    ClassWithLockInStaticConstructor2.Touch();
                });

                t.Start();
                ClassWithLockInStaticConstructor2.Touch();
                t.Join();
            },
            this.GetConfiguration().WithTestingIterations(10));
        }

        private class ClassWithLockInStaticConstructor
        {
            private static readonly object SyncObject = new object();
            public static bool IsInitialized { get; private set; }

            static ClassWithLockInStaticConstructor()
            {
                lock (SyncObject)
                {
                    IsInitialized = true;
                }
            }
        }

        private class ClassWithSchedulingPointInStaticConstructor
        {
            public static bool IsInitialized { get; private set; }

            static ClassWithSchedulingPointInStaticConstructor()
            {
                SchedulingPoint.Interleave();
                IsInitialized = true;
            }
        }

        private class ClassWithLockInStaticConstructor2
        {
            private static readonly object SyncObject = new object();
            public static bool IsInitialized { get; private set; }

            static ClassWithLockInStaticConstructor2()
            {
                lock (SyncObject)
                {
                    IsInitialized = true;
                }
            }

            public static void Touch()
            {
                // Force the static constructor to run by accessing the type.
                Specification.Assert(IsInitialized, "Static constructor should have completed.");
            }
        }
    }
}
