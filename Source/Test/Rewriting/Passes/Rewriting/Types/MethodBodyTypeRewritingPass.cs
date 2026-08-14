// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Rewriting.Types;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace Microsoft.Coyote.Rewriting
{
    internal sealed class MethodBodyTypeRewritingPass : TypeRewritingPass
    {
        /// <summary>
        /// Instructions whose operand is a type token describing the layout of the value being
        /// operated on, rather than merely naming a type.
        /// </summary>
        /// <remarks>
        /// These must be rewritten alongside the locals and fields they operate on. A rewritten type
        /// is a different struct of a different size, so leaving the token behind makes the runtime
        /// copy or interpret the wrong number of bytes: boxing the result of a redirected
        /// 'ConfigureAwait' under the original token, for instance, produces an object that reports
        /// the BCL type over the controlled type's storage.
        /// </remarks>
        private static readonly HashSet<OpCode> TypeTokenOpCodes = new HashSet<OpCode>
        {
            OpCodes.Box,
            OpCodes.Unbox,
            OpCodes.Unbox_Any,
            OpCodes.Initobj,
            OpCodes.Ldobj,
            OpCodes.Stobj,
            OpCodes.Sizeof,
            OpCodes.Constrained
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="MethodBodyTypeRewritingPass"/> class.
        /// </summary>
        internal MethodBodyTypeRewritingPass(RewritingOptions options, IEnumerable<AssemblyInfo> visitedAssemblies, LogWriter logWriter)
            : base(options, visitedAssemblies, logWriter)
        {
        }

        /// <inheritdoc/>
        protected internal override void VisitMethod(MethodDefinition method)
        {
            // Skip static constructors (.cctor) to avoid deadlocks: the CLR holds a native
            // type initialization lock during .cctor execution, and rewriting calls like
            // Monitor.Enter to Coyote's scheduler-aware versions could suspend the thread
            // while that lock is held, causing an unrecoverable deadlock (see issue #488).
            if (method is null || (method.IsConstructor && method.IsStatic))
            {
                return;
            }

            base.VisitMethod(method);
        }

        /// <inheritdoc/>
        protected override void VisitVariable(VariableDefinition variable)
        {
            if (this.Method is null)
            {
                return;
            }

            if (this.TryRewriteType(variable.VariableType, out TypeReference newVariableType) &&
                this.TryResolve(newVariableType, out TypeDefinition _))
            {
                this.LogWriter.LogDebug("............. [-] variable '{0}'", variable.VariableType);
                variable.VariableType = newVariableType;
                this.LogWriter.LogDebug("............. [+] variable '{0}'", variable.VariableType);
            }
        }

        /// <inheritdoc/>
        protected override Instruction VisitInstruction(Instruction instruction)
        {
            if (this.Method is null)
            {
                return instruction;
            }

            // Note that the C# compiler is not generating `OpCodes.Calli` instructions:
            // https://docs.microsoft.com/en-us/archive/blogs/shawnfa/calli-is-not-verifiable.
            // TODO: what about ldsfld, for static fields?
            if (instruction.OpCode == OpCodes.Stfld || instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldflda)
            {
                if (instruction.Operand is FieldDefinition fd &&
                    this.TryRewriteType(fd.FieldType, out TypeReference newFieldType) &&
                    this.TryResolve(newFieldType, out TypeDefinition _))
                {
                    this.LogWriter.LogDebug("............. [-] {0}", instruction);
                    fd.FieldType = newFieldType;
                    this.IsMethodBodyModified = true;
                    this.LogWriter.LogDebug("............. [+] {0}", instruction);
                }
                else if (instruction.Operand is FieldReference fr &&
                    this.TryRewriteType(fr.FieldType, out newFieldType) &&
                    this.TryResolve(newFieldType, out TypeDefinition _))
                {
                    this.LogWriter.LogDebug("............. [-] {0}", instruction);
                    fr.FieldType = newFieldType;
                    this.IsMethodBodyModified = true;
                    this.LogWriter.LogDebug("............. [+] {0}", instruction);
                }
            }
            else if (TypeTokenOpCodes.Contains(instruction.OpCode))
            {
                instruction = this.VisitTypeTokenInstruction(instruction);
            }
            else if (instruction.OpCode == OpCodes.Newobj)
            {
                instruction = this.VisitNewobjInstruction(instruction);
            }
            else if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                instruction.Operand is MethodReference methodReference)
            {
                instruction = this.VisitCallInstruction(instruction, methodReference);
            }

            return instruction;
        }

        /// <summary>
        /// Rewrites the type token operand of the specified instruction.
        /// </summary>
        /// <returns>The unmodified instruction, or the newly replaced instruction.</returns>
        private Instruction VisitTypeTokenInstruction(Instruction instruction)
        {
            TypeReference type = instruction.Operand as TypeReference;
            if (this.TryRewriteType(type, out TypeReference newType) &&
                this.TryResolve(newType, out TypeDefinition _))
            {
                var newInstruction = Instruction.Create(instruction.OpCode, newType);
                newInstruction.Offset = instruction.Offset;

                this.LogWriter.LogDebug("............. [-] {0}", instruction);
                this.Replace(instruction, newInstruction);
                this.LogWriter.LogDebug("............. [+] {0}", newInstruction);

                instruction = newInstruction;
            }

            return instruction;
        }

        /// <summary>
        /// Rewrites the specified <see cref="OpCodes.Newobj"/> instruction.
        /// </summary>
        /// <returns>The unmodified instruction, or the newly replaced instruction.</returns>
        private Instruction VisitNewobjInstruction(Instruction instruction)
        {
            MethodReference constructor = instruction.Operand as MethodReference;
            if (this.TryRewriteMethodReference(constructor, "Create", out MethodReference newMethod) &&
                this.TryResolve(newMethod, out MethodDefinition _))
            {
                // Create and return the new instruction.
                Instruction newInstruction = Instruction.Create(OpCodes.Call, newMethod);
                newInstruction.Offset = instruction.Offset;

                this.LogWriter.LogDebug("............. [-] {0}", instruction);
                this.Replace(instruction, newInstruction);
                this.LogWriter.LogDebug("............. [+] {0}", newInstruction);

                instruction = newInstruction;
            }

            return instruction;
        }

        /// <summary>
        /// Rewrites the specified non-generic <see cref="OpCodes.Call"/> or <see cref="OpCodes.Callvirt"/> instruction.
        /// </summary>
        /// <returns>The unmodified instruction, or the newly replaced instruction.</returns>
        private Instruction VisitCallInstruction(Instruction instruction, MethodReference method)
        {
#if NET
            if (instruction.Previous?.OpCode == OpCodes.Constrained &&
                instruction.Previous.Operand is TypeReference constrainedType &&
                (method.DeclaringType.FullName == NameCache.IHostedService ||
                 method.DeclaringType.FullName == NameCache.IHost) &&
                this.TryRewriteConstrainedHostingCall(method, constrainedType, out MethodReference constrainedMethod))
            {
                Instruction prefix = instruction.Previous;
                this.Replace(prefix, Instruction.Create(OpCodes.Nop));
                Instruction newInstruction = Instruction.Create(OpCodes.Call, constrainedMethod);
                newInstruction.Offset = instruction.Offset;
                this.LogWriter.LogDebug("............. [-] {0}", instruction);
                this.Replace(instruction, newInstruction);
                this.LogWriter.LogDebug("............. [+] {0}", newInstruction);
                return newInstruction;
            }

            if (instruction.Previous?.OpCode == OpCodes.Constrained &&
                (method.DeclaringType.FullName == NameCache.IDisposable
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER
                || method.DeclaringType.FullName == NameCache.IAsyncDisposable
#endif
                ))
            {
                // A constrained interface call consumes the address of a value type. The disposable
                // router consumes an interface reference, so replacing this call would leave both the
                // constrained prefix and the wrong stack shape behind. Value types cannot be a modelled
                // BackgroundService or IHost, so preserving their native dispatch loses no coverage.
                return instruction;
            }
#endif

            MethodReference newMethod = method;
            bool isRewritten = false;
#if NET
            bool isBaseCall = false;
#endif
            if (instruction.OpCode == OpCodes.Call && this.TryResolve(method, out MethodDefinition originalMethod) &&
                originalMethod.IsVirtual)
            {
                // A virtual 'call' is a base call. Static models otherwise erase the distinction between
                // that and 'callvirt', which makes an override that awaits before calling base redispatch
                // to itself after the model's temporary call stack has unwound.
                isRewritten = this.TryRewriteMethodReference(method, method.Name + "Base", out newMethod);
#if NET
                isBaseCall = isRewritten;
#endif
            }

            if (!isRewritten)
            {
                isRewritten = this.TryRewriteMethodReference(method, out newMethod);
            }

            if (isRewritten &&
                this.TryResolve(newMethod, out MethodDefinition resolvedMethod))
            {
#if NET
                if (isBaseCall)
                {
                    return this.RewriteBaseCall(instruction, method, newMethod);
                }
#endif

                // Create and return the new instruction.
                // Cecil marks value-type interface implementations as virtual, but a direct member
                // call on the value-type receiver still requires 'call'. Emitting 'callvirt' here
                // produces unverifiable IL because the stack contains the receiver address.
                bool useVirtualDispatch = resolvedMethod.IsVirtual &&
                    !resolvedMethod.DeclaringType.IsValueType;
                Instruction newInstruction = Instruction.Create(useVirtualDispatch ?
                    OpCodes.Callvirt : OpCodes.Call, newMethod);

                newInstruction.Offset = instruction.Offset;
                this.LogWriter.LogDebug("............. [-] {0}", instruction);
                this.Replace(instruction, newInstruction);
                this.LogWriter.LogDebug("............. [+] {0}", newInstruction);

                instruction = newInstruction;
            }

            return instruction;
        }

#if NET
        /// <summary>Finds and closes a by-reference generic router for a constrained hosting call.</summary>
        private bool TryRewriteConstrainedHostingCall(MethodReference originalMethod, TypeReference constrainedType,
            out MethodReference result)
        {
            result = originalMethod;
            Type providerType = originalMethod.DeclaringType.FullName == NameCache.IHost ?
                typeof(Types.Hosting.Host) : typeof(Types.Hosting.HostedService);
            TypeDefinition provider = this.Module.ImportReference(providerType).Resolve();
            MethodDefinition router = provider?.Methods.FirstOrDefault(candidate =>
                candidate.Name == originalMethod.Name &&
                candidate.HasGenericParameters &&
                candidate.GenericParameters.Count is 1 &&
                candidate.Parameters.Count == originalMethod.Parameters.Count + 1 &&
                candidate.Parameters[0].ParameterType is ByReferenceType);
            if (router is null)
            {
                return false;
            }

            var closed = new GenericInstanceMethod(this.Module.ImportReference(router));
            closed.GenericArguments.Add(this.Module.ImportReference(constrainedType));
            result = this.Module.ImportReference(closed);
            return true;
        }
#endif

#if NET
        /// <summary>
        /// Rewrites a nonvirtual base call so normal execution keeps the original CLR dispatch while
        /// systematic execution enters the controlled base model.
        /// </summary>
        private Instruction RewriteBaseCall(Instruction instruction, MethodReference originalMethod,
            MethodReference controlledMethod)
        {
            MethodReference predicate = this.TryImportMethod(
                typeof(Types.Hosting.BackgroundService), nameof(Types.Hosting.BackgroundService.IsExecutionControlled));
            if (predicate is null)
            {
                return instruction;
            }

            var instructions = new List<Instruction>();
            instructions.Add(this.Processor.Create(OpCodes.Call, predicate));
            Instruction controlledStart = this.Processor.Create(OpCodes.Call, controlledMethod);
            Instruction end = this.Processor.Create(OpCodes.Nop);
            instructions.Add(this.Processor.Create(OpCodes.Brtrue, controlledStart));
            instructions.Add(this.Processor.Create(OpCodes.Call, originalMethod));
            instructions.Add(this.Processor.Create(OpCodes.Br, end));
            instructions.Add(controlledStart);
            instructions.Add(end);

            this.LogWriter.LogDebug("............. [-] {0}", instruction);
            Instruction current = instructions[0];
            this.Replace(instruction, current);
            for (int idx = 1; idx < instructions.Count; ++idx)
            {
                this.Processor.InsertAfter(current, instructions[idx]);
                current = instructions[idx];
            }

            this.LogWriter.LogDebug("............. [+] guarded base call to {0}", originalMethod);
            return end;
        }
#endif

        /// <summary>
        /// Tries to rewrite the specified <see cref="MethodReference"/>.
        /// </summary>
        private bool TryRewriteMethodReference(MethodReference method, out MethodReference result) =>
            this.TryRewriteMethodReference(method, null, out result);

        /// <summary>
        /// Tries to rewrite the specified <see cref="MethodReference"/>.
        /// </summary>
        private bool TryRewriteMethodReference(MethodReference method, string matchName, out MethodReference result)
        {
            result = method;
            TypeDefinition resolvedDeclaringType = method.DeclaringType.Resolve();
            if (!this.IsRewritableType(resolvedDeclaringType))
            {
                return false;
            }

            // Variable that is passed by reference to rewriting methods keeping
            // track if any type in the method was rewritten.
            bool isRewritten = false;
            if (!this.TryResolve(method, out MethodDefinition resolvedMethod, false))
            {
                // Check if this method signature has been rewritten in the dependency assembly and,
                // if it has, find the rewritten method. The signature does not include the return
                // type according to C# rules, so we do not take it into account.
                List<TypeReference> paramTypes = new List<TypeReference>();
                for (int i = 0; i < method.Parameters.Count; ++i)
                {
                    var p = method.Parameters[i];
                    paramTypes.Add(this.RewriteType(p.ParameterType, Options.None));
                }

                MethodDefinition match = FindMethod(method.Name, resolvedDeclaringType, paramTypes.ToArray());
                if (!this.TryResolve(match, out resolvedMethod))
                {
                    // Unable to resolve the method or a rewritten version of this method.
                    return false;
                }

                isRewritten = true;
            }

            // Try to rewrite the declaring type.
            TypeReference newDeclaringType = this.RewriteType(method.DeclaringType,
                Options.AllowStaticRewrittenType, ref isRewritten);
            if (!this.TryResolve(newDeclaringType, out TypeDefinition resolvedNewDeclaringType))
            {
                // Unable to resolve the declaring type of the method.
                return false;
            }

            bool isDeclaringTypeRewritten = IsRuntimeType(resolvedNewDeclaringType);
            if (isDeclaringTypeRewritten)
            {
                // The declaring type is being rewritten, so only rewrite the return and
                // parameter types if they are generic.
                // resolvedMethod = FindMatchingMethodInDeclaringType(resolvedNewDeclaringType, resolvedMethod, matchName);
                if (!this.TryFindMethod(resolvedNewDeclaringType, resolvedMethod, matchName, out resolvedMethod))
                {
                    // No matching method found.
                    return false;
                }

                result = resolvedMethod;
            }

            if (!result.HasThis && !newDeclaringType.IsGenericInstance &&
                method.HasThis && method.DeclaringType.IsGenericInstance)
            {
                // TODO: is this needed?

                // We are converting from a generic type to a non generic static type, and from a non-generic
                // method to a generic method, so we need to instantiate the generic method.
                GenericInstanceMethod genericInstanceMethod = new GenericInstanceMethod(result);

                var genericArgs = new List<TypeReference>();
                if (method.DeclaringType is GenericInstanceType genericDeclaringType)
                {
                    // Populate the generic arguments with the generic declaring type arguments.
                    genericArgs.AddRange(genericDeclaringType.GenericArguments);
                    foreach (var genericArg in genericArgs)
                    {
                        genericInstanceMethod.GenericArguments.Add(genericArg);
                    }
                }

                result = genericInstanceMethod;
            }
            else
            {
                // Try rewrite the return only if the declaring type is a non-runtime type,
                // else assign the generic arguments if the parameter is generic.
                TypeReference newReturnType = this.RewriteType(resolvedMethod.ReturnType,
                    isDeclaringTypeRewritten ? Options.SkipRootType : Options.None, ref isRewritten);

                // Instantiate the method reference to set its generic arguments and parameters, if any.
                result = new MethodReference(result.Name, newReturnType, newDeclaringType)
                {
                    HasThis = result.HasThis,
                    ExplicitThis = result.ExplicitThis,
                    CallingConvention = result.CallingConvention
                };

                if (resolvedMethod.HasGenericParameters && method is GenericInstanceMethod genericInstanceMethod)
                {
                    // Need to rewrite the generic method to instantiate the correct generic parameter types.
                    result = this.RewriteGenericArguments(result, resolvedMethod.GenericParameters,
                        genericInstanceMethod.GenericArguments, ref isRewritten);
                }

                // Rewrite the parameters of the method, if any.
                result = this.RewriteParameters(result, resolvedMethod.Parameters, ref isRewritten);
            }

            result = this.Module.ImportReference(result);
            return isRewritten;
        }

        /// <summary>
        /// Rewrites the generic arguments of the specified <see cref="MethodReference"/>.
        /// </summary>
        private MethodReference RewriteGenericArguments(MethodReference method, Collection<GenericParameter> genericParameters,
            Collection<TypeReference> genericArguments, ref bool isRewritten)
        {
            var genericMethod = new GenericInstanceMethod(method);
            for (int i = 0; i < genericArguments.Count; ++i)
            {
                GenericParameter parameter = new GenericParameter(genericParameters[i].Name, genericMethod);
                method.GenericParameters.Add(parameter);
                genericMethod.GenericParameters.Add(parameter);

                TypeReference newArgType = this.RewriteType(genericArguments[i], Options.None, ref isRewritten);
                genericMethod.GenericArguments.Add(newArgType);
            }

            return genericMethod;
        }

        /// <summary>
        /// Rewrites the parameters of the specified <see cref="MethodReference"/>.
        /// </summary>
        private MethodReference RewriteParameters(MethodReference method, Collection<ParameterDefinition> parameters,
            ref bool isRewritten)
        {
            for (int i = 0; i < parameters.Count; ++i)
            {
                // Try rewrite the parameter only if the declaring type is a non-runtime type,
                // else assign the generic arguments if the parameter is generic.
                ParameterDefinition parameter = parameters[i];
                bool isDeclaringTypeRewritten = IsRuntimeType(method.DeclaringType);
                TypeReference newParameterType = this.RewriteType(parameter.ParameterType,
                    IsRuntimeType(method.DeclaringType) ? Options.SkipRootType : Options.None, ref isRewritten);
                ParameterDefinition newParameter = new ParameterDefinition(parameter.Name,
                    parameter.Attributes, newParameterType);
                method.Parameters.Add(newParameter);
            }

            return method;
        }

        /// <summary>
        /// Finds the matching method in the specified declaring type, if any.
        /// </summary>
        private bool TryFindMethod(TypeDefinition declaringType, MethodDefinition originalMethod,
            string matchName, out MethodDefinition match)
        {
            // Computed once per lookup rather than per candidate: rewriting a type imports it into
            // the module, so doing this inside the loop would grow the type reference table with
            // every method that is considered and then rejected.
            TypeReference expectedReturnType = originalMethod.IsConstructor ?
                originalMethod.DeclaringType : this.RewriteType(originalMethod.ReturnType, Options.None);

            match = null;
            foreach (var method in declaringType.Methods)
            {
                bool isMatch = matchName != null ?
                    method.Name == matchName && CheckMethodSignaturesMatch(
                        originalMethod, method, expectedReturnType, ignoreName: true) :
                    CheckMethodSignaturesMatch(originalMethod, method, expectedReturnType);
                if (isMatch)
                {
                    match = method;
                    break;
                }
            }

            return match != null;
        }

        /// <summary>
        /// Checks whether the specified parameter can be the instance that the replaced method would
        /// have run on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The parameter is written in the replacement's terms and the declaring type in the original's,
        /// so the two are never the same reference and there is nothing to compare but the name of what
        /// they ultimately denote. This is why the check that used to be here did nothing: it compared
        /// the two references for identity, which no pair of them can ever satisfy, so the receiver was
        /// never validated at all. Inverting it, as the shape of the code invites, would reject every
        /// instance-to-static replacement in existence.
        /// </para>
        /// <para>
        /// A replacement legitimately writes the receiver as a constructed instance of the type that
        /// declares the original, by reference, or as an 'in' parameter, which is a required modifier
        /// over a reference — so those wrappers are peeled. An array is NOT peeled: a first parameter
        /// that is an array of receivers is taking a collection, as <c>WaitHandle.WaitAll</c> does, and
        /// accepting it would redirect an instance call site onto a method that consumes something else.
        /// </para>
        /// <para>
        /// A parameter that will not resolve is accepted rather than rejected. The point of this check
        /// is to catch a replacement that names the wrong type, not to re-verify the assembly graph, and
        /// failing closed would silently drop replacements that work today whenever a reference cannot
        /// be followed.
        /// </para>
        /// </remarks>
        internal static bool IsReceiverParameter(TypeReference parameterType, TypeReference declaringType)
        {
            TypeReference receiver = parameterType;
            while (receiver is RequiredModifierType || receiver is OptionalModifierType ||
                receiver is ByReferenceType)
            {
                receiver = ((TypeSpecification)receiver).ElementType;
            }

            if (receiver is GenericInstanceType genericReceiver)
            {
                receiver = genericReceiver.ElementType;
            }

            if (receiver.FullName == declaringType.FullName)
            {
                return true;
            }

            if (receiver is TypeSpecification)
            {
                // Whatever is left after peeling is something built out of a type rather than the type:
                // an array of it, or a pointer to it. Resolving one answers for its element type, which
                // would accept exactly the shapes peeling deliberately left alone.
                return false;
            }

            TypeDefinition resolvedReceiver = receiver.Resolve();
            return resolvedReceiver is null || resolvedReceiver.FullName == declaringType.FullName;
        }

        /// <summary>
        /// Checks if the parameters of the two specified methods match.
        /// </summary>
        private static bool CheckMethodParametersMatch(MethodDefinition left, MethodDefinition right)
        {
            if (left.Parameters.Count != right.Parameters.Count)
            {
                return false;
            }

            for (int idx = 0; idx < right.Parameters.Count; ++idx)
            {
                var leftParam = left.Parameters[idx];
                var rightParam = right.Parameters[idx];
                // TODO: make sure all necessary checks are in place!
                if ((leftParam.ParameterType.FullName != rightParam.ParameterType.FullName) ||
                    (leftParam.Name != rightParam.Name) ||
                    (leftParam.IsIn && !rightParam.IsIn) ||
                    (leftParam.IsOut && !rightParam.IsOut))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks if the signatures of the original and the replacement methods match.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method also checks the use case where we are converting an instance method into a static method.
        /// In such a case case, we are inserting a first parameter that has the same type as the declaring type
        /// of the original method.
        /// </para>
        /// <para>
        /// The return type is compared against the REWRITTEN return type of the original method, not the
        /// original one: a replacement legitimately returns the modelled counterpart of what it replaces,
        /// as <c>GetAwaiter</c> and <c>ConfigureAwait</c> do. Comparing it at all is what stops a
        /// replacement whose return type is merely wrong from being accepted: the parameters would still
        /// line up, the call site would be redirected, and the emitted call would leave the evaluation
        /// stack short by exactly one slot. Rejecting the match instead leaves the call unrewritten,
        /// which loses the modelling but keeps the assembly verifiable.
        /// </para>
        /// </remarks>
        private static bool CheckMethodSignaturesMatch(MethodDefinition originalMethod, MethodDefinition newMethod,
            TypeReference expectedReturnType, bool ignoreName = false)
        {
            // TODO: make sure all necessary checks are in place!
            // Check if the method properties match. We check 'IsStatic' later as we need to do additional checks
            // in cases where we are replacing an instance method with a static method.
            bool isFactoryReplacement = ignoreName && originalMethod.IsConstructor &&
                newMethod.IsStatic && newMethod.Name == "Create";
            bool hasExpectedReturnType = isFactoryReplacement ?
                HaveEquivalentFactoryTypes(originalMethod.DeclaringType, newMethod.ReturnType) :
                expectedReturnType.FullName == newMethod.ReturnType.FullName;
            if ((!ignoreName && originalMethod.Name != newMethod.Name) ||
                (originalMethod.IsConstructor != newMethod.IsConstructor && !isFactoryReplacement) ||
                !hasExpectedReturnType ||
                originalMethod.IsPublic != newMethod.IsPublic ||
                originalMethod.IsPrivate != newMethod.IsPrivate ||
                originalMethod.IsAssembly != newMethod.IsAssembly ||
                originalMethod.IsFamilyAndAssembly != newMethod.IsFamilyAndAssembly)
            {
                return false;
            }

            // Check if we are converting the original method into a static method.
            bool isConvertedToStatic = !originalMethod.IsStatic && newMethod.IsStatic;
            int parameterCountDiff = newMethod.Parameters.Count - originalMethod.Parameters.Count;
            if (isFactoryReplacement)
            {
                if (parameterCountDiff != 0)
                {
                    return false;
                }
            }
            else if (isConvertedToStatic)
            {
                // We are expecting one extra parameter in the static method in index '0', and the type
                // of this parameter must be the same as the declaring type of the original method.
                if (parameterCountDiff != 1 ||
                    !IsReceiverParameter(newMethod.Parameters[0].ParameterType, originalMethod.DeclaringType))
                {
                    return false;
                }
            }
            else if (originalMethod.IsStatic != newMethod.IsStatic || parameterCountDiff != 0)
            {
                // The static properties or the parameter counts do not match.
                return false;
            }

            // Check if the parameters match.
            for (int idx = 0; idx < originalMethod.Parameters.Count; ++idx)
            {
                // If we are converting to static, we have one extra parameter, so skip it.
                var newParameter = newMethod.Parameters[isConvertedToStatic && !isFactoryReplacement ? idx + 1 : idx];
                var originalParameter = originalMethod.Parameters[idx];

                // TODO: make sure all necessary checks are in place!
                if ((newParameter.ParameterType.FullName != originalParameter.ParameterType.FullName) ||
                    (newParameter.Name != originalParameter.Name) ||
                    (newParameter.IsIn && !originalParameter.IsIn) ||
                    (newParameter.IsOut && !originalParameter.IsOut))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks whether a constructor factory returns the type that the constructor creates.
        /// </summary>
        /// <remarks>
        /// Cecil resolves a constructor on a generic type to the open declaring definition, while a
        /// factory declares a constructed return such as <c>BlockingCollection&lt;T&gt;</c>. Their literal
        /// full names differ even though they describe the same type. Compare the generic definitions and
        /// require each argument to preserve its position so that a factory returning a closed or permuted
        /// generic type is still rejected.
        /// </remarks>
        private static bool HaveEquivalentFactoryTypes(TypeReference expected, TypeReference actual)
        {
            TypeReference expectedDefinition = expected;
            IList<GenericParameter> expectedParameters = expected.GenericParameters;
            if (expected is GenericInstanceType expectedInstance)
            {
                expectedDefinition = expectedInstance.ElementType;
            }

            TypeReference actualDefinition = actual;
            IList<TypeReference> actualArguments = Array.Empty<TypeReference>();
            if (actual is GenericInstanceType actualInstance)
            {
                actualDefinition = actualInstance.ElementType;
                actualArguments = actualInstance.GenericArguments;
            }

            if (expectedDefinition.FullName != actualDefinition.FullName ||
                expectedParameters.Count != actualArguments.Count)
            {
                return false;
            }

            for (int idx = 0; idx < expectedParameters.Count; ++idx)
            {
                if (!(actualArguments[idx] is GenericParameter argument) || argument.Position != idx)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
