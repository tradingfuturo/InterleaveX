// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Coyote.Logging;
using Microsoft.Coyote.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Microsoft.Coyote.Rewriting
{
    /// <summary>
    /// Rewriting pass that adds scheduling points at memory-access locations.
    /// </summary>
    internal class MemoryAccessRewritingPass : RewritingPass
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryAccessRewritingPass"/> class.
        /// </summary>
        internal MemoryAccessRewritingPass(IEnumerable<AssemblyInfo> visitedAssemblies, LogWriter logWriter)
            : base(visitedAssemblies, logWriter)
        {
        }

        /// <inheritdoc/>
        protected internal override void VisitMethod(MethodDefinition method)
        {
            if (this.IsAsyncStateMachineType ||
                method is null || method.IsConstructor ||
                method.IsGetter || method.IsSetter)
            {
                return;
            }

            base.VisitMethod(method);
        }

        /// <inheritdoc/>
        protected override Instruction VisitInstruction(Instruction instruction)
        {
            if (this.Method is null)
            {
                return instruction;
            }

            try
            {
                if (instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Stfld)
                {
                    this.LogWriter.LogDebug("............. [+] injected scheduling point at field-access instruction");

                    MethodReference providerMethod = this.TryImportMethod(
                        typeof(SchedulingPoint), nameof(SchedulingPoint.InterleaveMemoryAccess));
                    if (providerMethod is null)
                    {
                        return instruction;
                    }

                    if (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Volatile)
                    {
                        this.Processor.InsertBefore(instruction.Previous, Instruction.Create(OpCodes.Call, providerMethod));
                    }
                    else
                    {
                        this.Processor.InsertBefore(instruction, Instruction.Create(OpCodes.Call, providerMethod));
                    }

                    this.IsMethodBodyModified = true;
                }
                else if (instruction.OpCode == OpCodes.Brfalse || instruction.OpCode == OpCodes.Brfalse_S ||
                    instruction.OpCode == OpCodes.Brtrue || instruction.OpCode == OpCodes.Brtrue_S)
                {
                    this.LogWriter.LogDebug("............. [+] injected scheduling point at branching instruction");

                    MethodReference providerMethod = this.TryImportMethod(
                        typeof(SchedulingPoint), nameof(SchedulingPoint.InterleaveControlFlow));
                    if (providerMethod is null)
                    {
                        return instruction;
                    }

                    this.Processor.InsertBefore(instruction, Instruction.Create(OpCodes.Call, providerMethod));

                    this.IsMethodBodyModified = true;
                }
            }
            catch (AssemblyResolutionException)
            {
                // Skip this instruction, we are only interested in types that can be resolved.
            }

            return instruction;
        }
    }
}
