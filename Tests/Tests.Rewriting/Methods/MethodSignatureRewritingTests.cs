// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Coyote.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Rewriting.Tests
{
    public class MethodSignatureRewritingTests : BaseRewritingTest
    {
        public MethodSignatureRewritingTests(ITestOutputHelper output)
            : base(output)
        {
        }

        private static TaskAwaiter GetTaskAwaiter(TaskAwaiter taskAwaiter) => taskAwaiter;
        private static TaskAwaiter<T> GetGenericTaskAwaiter<T>(TaskAwaiter<T> taskAwaiter) => taskAwaiter;

        [Fact(Timeout = 5000)]
        public void TestRewritingTaskAwaiterInMethodSignature()
        {
            GetTaskAwaiter(default(TaskAwaiter));
        }

        [Fact(Timeout = 5000)]
        public void TestRewritingGenericTaskAwaiterInMethodSignature()
        {
            GetGenericTaskAwaiter<int>(default(TaskAwaiter<int>));
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRewritingVirtualValueTypeMemberUsesValidDispatch()
        {
            var awaiter = new ValueTask<int>(0).GetAwaiter();
            awaiter.OnCompleted(() => { });
            Assert.Equal(0, awaiter.GetResult());
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRewritingConstrainedValueTypeMemberPreservesVirtualDispatch()
        {
            using var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("ConstrainedDispatch", new Version(1, 0)),
                "ConstrainedDispatch", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Dispatch",
                TypeAttributes.Class | TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Invoke",
                MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            TypeReference awaiterType = module.ImportReference(typeof(ValueTaskAwaiter));
            method.Body.Variables.Add(new VariableDefinition(awaiterType));
            var processor = method.Body.GetILProcessor();
            processor.Append(Instruction.Create(OpCodes.Ldloca_S, method.Body.Variables[0]));
            processor.Append(Instruction.Create(OpCodes.Ldnull));
            processor.Append(Instruction.Create(OpCodes.Constrained, awaiterType));
            processor.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(
                typeof(ValueTaskAwaiter).GetMethod(nameof(ValueTaskAwaiter.OnCompleted)))));
            processor.Append(Instruction.Create(OpCodes.Ret));

            using var logWriter = new MemoryLogWriter(Coyote.Configuration.Create());
            var pass = new MethodBodyTypeRewritingPass(RewritingOptions.Create(),
                Array.Empty<AssemblyInfo>(), logWriter);
            pass.VisitAssembly(null);
            pass.VisitModule(module);
            pass.VisitType(type);
            pass.VisitMethod(method);

            Instruction rewritten = method.Body.Instructions.Single(
                instruction => instruction.Operand is MethodReference reference &&
                    reference.Name == nameof(ValueTaskAwaiter.OnCompleted));
            Assert.Equal(OpCodes.Constrained, rewritten.Previous.OpCode);
            Assert.Equal(OpCodes.Callvirt, rewritten.OpCode);
            Assert.Equal(typeof(Runtime.CompilerServices.ValueTaskAwaiter).FullName,
                ((MethodReference)rewritten.Operand).DeclaringType.FullName);
        }
    }
}
