// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or
// later. See LICENSE-GPL for the full text.

using Microsoft.Coyote.Runtime;
using Microsoft.Coyote.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Coyote.Runtime.Tests
{
    public abstract class BaseRuntimeTest : BaseTest
    {
        public BaseRuntimeTest(ITestOutputHelper output)
            : base(output)
        {
        }

        private protected override SchedulingPolicy SchedulingPolicy
        {
            get
            {
                return SchedulingPolicy.Interleaving;
            }
        }
    }
}
