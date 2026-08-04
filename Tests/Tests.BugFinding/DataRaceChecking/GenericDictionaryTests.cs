// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.BugFinding.Tests.DataRaceChecking
{
    public class GenericDictionaryTests : BaseBugFindingTest
    {
        public GenericDictionaryTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 5000)]
        public void TestGenericDictionaryAddDataRace()
        {
            this.TestWithError(async () =>
            {
                var dictionary = new Dictionary<int, bool>();

                Task t1 = Task.Run(() =>
                {
                    dictionary.Add(1, true);
                });

                Task t2 = Task.Run(() =>
                {
                    dictionary.Add(2, false);
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found write/write data race on '{typeof(Dictionary<int, bool>)}'.",
            replay: true);
        }

        [Fact(Timeout = 5000)]
        public void TestGenericDictionaryIndex()
        {
            this.Test(async () =>
            {
                var dictionary = new Dictionary<int, bool>
                {
                    { 1, true }
                };

                Task t1 = Task.Run(() =>
                {
                    _ = dictionary[1];
                });

                Task t2 = Task.Run(() =>
                {
                    _ = dictionary[1];
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }

        [Fact(Timeout = 5000)]
        public void TestGenericDictionaryIndexDataRace()
        {
            this.TestWithError(async () =>
            {
                var dictionary = new Dictionary<int, bool>
                {
                    { 1, true }
                };

                Task t1 = Task.Run(() =>
                {
                    _ = dictionary[1];
                });

                Task t2 = Task.Run(() =>
                {
                    dictionary[1] = false;
                });

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100),
            expectedError: $"Found read/write data race on '{typeof(Dictionary<int, bool>)}'.",
            replay: true);
        }

        /// <summary>
        /// Serializing reads the dictionary and changes nothing, so two operations serializing the same
        /// dictionary at once are two readers, and two readers are not a race.
        /// </summary>
        /// <remarks>
        /// Declared as a write, it reported one — the only case in this area where the guard invented a
        /// bug rather than missing one, and the more expensive kind to be wrong about. Each operation
        /// serializes into its own store because writing the same name into one store twice is an error
        /// in its own right, and would fail this test for a reason that has nothing to do with the guard.
        /// </remarks>
        [Fact(Timeout = 5000)]
        public void TestGenericDictionaryConcurrentSerializationIsNotARace()
        {
            this.Test(async () =>
            {
                var dictionary = new Dictionary<int, bool>
                {
                    { 1, true }
                };

#pragma warning disable SYSLIB0050 // Type or member is obsolete
#pragma warning disable SYSLIB0051 // Type or member is obsolete
                Task t1 = Task.Run(() =>
                {
                    dictionary.GetObjectData(
                        new SerializationInfo(typeof(Dictionary<int, bool>), new FormatterConverter()),
                        default);
                });

                Task t2 = Task.Run(() =>
                {
                    dictionary.GetObjectData(
                        new SerializationInfo(typeof(Dictionary<int, bool>), new FormatterConverter()),
                        default);
                });
#pragma warning restore SYSLIB0051 // Type or member is obsolete
#pragma warning restore SYSLIB0050 // Type or member is obsolete

                await Task.WhenAll(t1, t2);
            },
            configuration: this.GetConfiguration().WithTestingIterations(100));
        }
    }
}
