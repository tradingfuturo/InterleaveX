// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private static TaskAwaiter GetConstrainedTaskAwaiter<T>(ref T task)
            where T : Task => task.GetAwaiter();

        private static TaskAwaiter<TResult> GetConstrainedGenericTaskAwaiter<TTask, TResult>(ref TTask task)
            where TTask : Task<TResult> => task.GetAwaiter();

        private static bool TryAddToConstrainedGenericDictionary<TDictionary, TValue, TKey>(
            ref TDictionary dictionary, TKey key, TValue value)
            where TDictionary : ConcurrentDictionary<TKey, TValue> => dictionary.TryAdd(key, value);

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

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestRewritingConstrainedReferenceReceiverUsesValidStaticDispatch()
        {
            Task task = Task.CompletedTask;
            TaskAwaiter awaiter = GetConstrainedTaskAwaiter(ref task);
            Assert.True(awaiter.IsCompleted);
            awaiter.GetResult();
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "GenericConstrainedRewriteRemediation")]
        public void TestRewritingConstrainedGenericTaskPreservesConstructedType()
        {
            Task<int> task = Task.FromResult(7);
            TaskAwaiter<int> awaiter =
                GetConstrainedGenericTaskAwaiter<Task<int>, int>(ref task);
            Assert.True(awaiter.IsCompleted);
            Assert.Equal(7, awaiter.GetResult());
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "GenericConstrainedRewriteRemediation")]
        public void TestRewritingConstrainedGenericDictionaryPreservesConstructedTypes()
        {
            var dictionary = new ConcurrentDictionary<string, int>();
            Assert.True(TryAddToConstrainedGenericDictionary<ConcurrentDictionary<string, int>, int, string>(
                ref dictionary, "key", 7));
            Assert.Equal(7, dictionary["key"]);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "GenericConstrainedRewriteRemediation")]
        public void TestCecilRewritingConstrainedGenericRoutesUseCallerOwnedParameters()
        {
            using ModuleDefinition module = ModuleDefinition.ReadModule(this.GetType().Assembly.Location);
            TypeDefinition type = module.GetType(typeof(MethodSignatureRewritingTests).FullName);

            AssertGeneratedRouteUsesCallerOwnedParameters(type,
                nameof(GetConstrainedGenericTaskAwaiter), new[] { 1 });
            AssertGeneratedRouteUsesCallerOwnedParameters(type,
                nameof(TryAddToConstrainedGenericDictionary), new[] { 2, 1 });
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCecilRewritingConstrainedReferenceReceiverUsesStaticCall()
        {
            using var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("ConstrainedReferenceDispatch", new Version(1, 0)),
                "ConstrainedReferenceDispatch", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Dispatch",
                TypeAttributes.Class | TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Invoke",
                MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            var receiver = new GenericParameter("TReceiver", method);
            receiver.Constraints.Add(new GenericParameterConstraint(module.ImportReference(typeof(Task))));
            method.GenericParameters.Add(receiver);
            method.Parameters.Add(new ParameterDefinition("task", ParameterAttributes.None, receiver));
            var processor = method.Body.GetILProcessor();
            processor.Append(Instruction.Create(OpCodes.Ldarga_S, method.Parameters[0]));
            processor.Append(Instruction.Create(OpCodes.Constrained, receiver));
            processor.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(
                typeof(Task).GetMethod(nameof(Task.GetAwaiter)))));
            processor.Append(Instruction.Create(OpCodes.Pop));
            processor.Append(Instruction.Create(OpCodes.Ret));

            Rewrite(module, type, method);

            Instruction rewritten = method.Body.Instructions.Single(instruction =>
                instruction.Operand is MethodReference reference &&
                reference.Name == nameof(Task.GetAwaiter));
            Assert.Equal(OpCodes.Call, rewritten.OpCode);
            Assert.DoesNotContain(method.Body.Instructions,
                instruction => instruction.OpCode == OpCodes.Constrained);
        }

        [Fact(Timeout = 5000)]
        [Trait("Category", "RewritingRemediation")]
        public void TestCecilRewritingDirectValueTypeReceiverUsesStaticCall()
        {
            using var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("ConstrainedValueDispatch", new Version(1, 0)),
                "ConstrainedValueDispatch", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            var type = new TypeDefinition("Tests", "Dispatch",
                TypeAttributes.Class | TypeAttributes.Public, module.TypeSystem.Object);
            module.Types.Add(type);
            var method = new MethodDefinition("Invoke",
                MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            type.Methods.Add(method);
            TypeReference receiver = module.ImportReference(typeof(ValueTask));
            method.Body.Variables.Add(new VariableDefinition(receiver));
            var processor = method.Body.GetILProcessor();
            processor.Append(Instruction.Create(OpCodes.Ldloca_S, method.Body.Variables[0]));
            processor.Append(Instruction.Create(OpCodes.Constrained, receiver));
            processor.Append(Instruction.Create(OpCodes.Callvirt, module.ImportReference(
                typeof(ValueTask).GetMethod(nameof(ValueTask.GetAwaiter)))));
            processor.Append(Instruction.Create(OpCodes.Pop));
            processor.Append(Instruction.Create(OpCodes.Ret));

            Rewrite(module, type, method);

            Instruction rewritten = method.Body.Instructions.Single(instruction =>
                instruction.Operand is MethodReference reference &&
                reference.Name == nameof(ValueTask.GetAwaiter));
            Assert.Equal(OpCodes.Call, rewritten.OpCode);
            Assert.DoesNotContain(method.Body.Instructions,
                instruction => instruction.OpCode == OpCodes.Constrained);
        }

        private static void Rewrite(ModuleDefinition module, TypeDefinition type, MethodDefinition method)
        {
            using var logWriter = new MemoryLogWriter(Coyote.Configuration.Create());
            var pass = new MethodBodyTypeRewritingPass(RewritingOptions.Create(),
                Array.Empty<AssemblyInfo>(), logWriter);
            pass.VisitAssembly(null);
            pass.VisitModule(module);
            pass.VisitType(type);
            pass.VisitMethod(method);
        }

        private static void AssertGeneratedRouteUsesCallerOwnedParameters(
            TypeDefinition type, string methodName, int[] expectedReceiverArgumentPositions)
        {
            MethodDefinition method = type.Methods.Single(candidate => candidate.Name == methodName);
            IEnumerable<TypeReference> generatedTypes = method.Body.Variables.Select(variable => variable.VariableType)
                .Concat(method.Body.Instructions
                    .Where(instruction => instruction.OpCode == OpCodes.Ldobj ||
                        instruction.OpCode == OpCodes.Box || instruction.OpCode == OpCodes.Castclass)
                    .Select(instruction => (TypeReference)instruction.Operand));

            foreach (GenericParameter parameter in generatedTypes.SelectMany(GetGenericParameters))
            {
                Assert.Same(method, parameter.Owner);
            }

            var receiverType = (GenericInstanceType)method.Body.Instructions.Single(
                instruction => instruction.OpCode == OpCodes.Castclass).Operand;
            Assert.Equal(expectedReceiverArgumentPositions,
                receiverType.GenericArguments.Cast<GenericParameter>().Select(argument => argument.Position));
        }

        private static IEnumerable<GenericParameter> GetGenericParameters(TypeReference type)
        {
            if (type is GenericParameter parameter)
            {
                yield return parameter;
            }
            else if (type is GenericInstanceType genericType)
            {
                foreach (GenericParameter argument in genericType.GenericArguments.SelectMany(GetGenericParameters))
                {
                    yield return argument;
                }
            }
            else if (type is TypeSpecification specification)
            {
                foreach (GenericParameter element in GetGenericParameters(specification.ElementType))
                {
                    yield return element;
                }
            }
        }
    }
}
