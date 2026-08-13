// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
// Modifications are licensed under the GNU General Public License v3.0 or later.

#if NET
#pragma warning disable CS1591 // Compiler-facing rewrite shims mirror framework signatures.
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using SystemBackgroundService = Microsoft.Extensions.Hosting.BackgroundService;

namespace Microsoft.Coyote.Rewriting.Types
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Disposable
    {
        public static void Dispose(IDisposable instance)
        {
            if (instance is SystemBackgroundService service)
            {
                Hosting.BackgroundService.Dispose(service);
            }
            else if (instance is IHost host)
            {
                Hosting.Host.Dispose(host);
            }
            else
            {
                instance.Dispose();
            }
        }
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class AsyncDisposable
    {
        public static ValueTask DisposeAsync(IAsyncDisposable instance)
        {
            if (instance is IHost host)
            {
                return Hosting.Host.DisposeAsync(host);
            }

            return instance.DisposeAsync();
        }
    }
}
#pragma warning restore CS1591
#endif
