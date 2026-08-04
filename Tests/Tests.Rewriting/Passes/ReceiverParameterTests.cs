// Copyright (c) 2026 pipflow.com <https://pipflow.com>
//
// This file is part of InterleaveX and is licensed under the GNU General
// Public License v3.0 or later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Threading;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    /// <summary>
    /// Tests which first parameter of a replacement counts as the instance the replaced method would
    /// have run on.
    /// </summary>
    /// <remarks>
    /// The check this exercises spent its whole life comparing two type references for identity, which
    /// two references from different modules can never satisfy — so it accepted every receiver, however
    /// wrong, and no test could have noticed. These cases pin both directions: the shapes a replacement
    /// legitimately writes a receiver in, and the shapes that are not a receiver at all.
    /// </remarks>
    public class ReceiverParameterTests : BaseRewritingTest
    {
        public ReceiverParameterTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact(Timeout = 15000)]
        public void TestConstructedInstanceOfTheDeclaringTypeIsTheReceiver()
        {
            // How every collection replacement writes it: the method is found on the definition, and
            // the replacement takes an instance of it.
            this.CheckReceiver(typeof(List<int>), typeof(List<>), expected: true);
        }

        [Fact(Timeout = 15000)]
        public void TestReceiverPassedByReferenceIsTheReceiver()
        {
            // How a replacement for a method on a value type writes it, as the one for a lock scope does.
            this.WithModule(module =>
            {
                TypeReference declaringType = module.ImportReference(typeof(SpinWait)).Resolve();
                var byReference = new ByReferenceType(module.ImportReference(typeof(SpinWait)));
                Assert.True(MethodBodyTypeRewritingPass.IsReceiverParameter(byReference, declaringType));
            });
        }

        [Fact(Timeout = 15000)]
        public void TestReceiverPassedAsInIsTheReceiver()
        {
            // An 'in' parameter is a required modifier wrapped around a reference, so both come off.
            this.WithModule(module =>
            {
                TypeReference declaringType = module.ImportReference(typeof(SpinWait)).Resolve();
                var asIn = new RequiredModifierType(
                    module.ImportReference(typeof(System.Runtime.InteropServices.InAttribute)),
                    new ByReferenceType(module.ImportReference(typeof(SpinWait))));
                Assert.True(MethodBodyTypeRewritingPass.IsReceiverParameter(asIn, declaringType));
            });
        }

        [Fact(Timeout = 15000)]
        public void TestAnotherTypeIsNotTheReceiver()
        {
            // The case the check exists for: a replacement that claims a method of one collection while
            // taking another. Accepting it would redirect the call site onto a method that cannot serve
            // it, which is how a wrong return type used to corrupt the emitted call.
            this.CheckReceiver(typeof(HashSet<int>), typeof(List<>), expected: false);
        }

        [Fact(Timeout = 15000)]
        public void TestArrayOfTheDeclaringTypeIsNotTheReceiver()
        {
            // An array of receivers is a collection, not a receiver: WaitHandle.WaitAll takes one, and
            // it replaces a static method rather than an instance one.
            this.WithModule(module =>
            {
                TypeReference declaringType = module.ImportReference(typeof(WaitHandle)).Resolve();
                var array = new ArrayType(module.ImportReference(typeof(WaitHandle)));
                Assert.False(MethodBodyTypeRewritingPass.IsReceiverParameter(array, declaringType));
            });
        }

        /// <summary>
        /// Checks whether a parameter of the first type would be accepted as the instance of a method
        /// declared by the second.
        /// </summary>
        private void CheckReceiver(Type parameterType, Type declaringType, bool expected) =>
            this.WithModule(module =>
            {
                TypeReference parameter = module.ImportReference(parameterType);
                TypeReference declaring = module.ImportReference(declaringType).Resolve();
                Assert.Equal(expected, MethodBodyTypeRewritingPass.IsReceiverParameter(parameter, declaring));
            });

        /// <summary>
        /// Runs the specified check against a module that can import the types it needs, which is the
        /// only way to obtain the type references the rewriter works in.
        /// </summary>
        private void WithModule(Action<ModuleDefinition> check)
        {
            using ModuleDefinition module = ModuleDefinition.ReadModule(this.GetType().Assembly.Location);
            check(module);
        }
    }
}
