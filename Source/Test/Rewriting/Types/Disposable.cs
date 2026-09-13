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
using SystemMutex = System.Threading.Mutex;

namespace Microsoft.Coyote.Rewriting.Types
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Disposable
    {
        public static void Dispose(IDisposable instance)
        {
            if (instance is SystemBackgroundService service &&
                Hosting.BackgroundService.UsesDisposableSlot(service))
            {
                Hosting.BackgroundService.Dispose(service);
            }
            else if (instance is IHost host)
            {
                Hosting.Host.Dispose(host);
            }
            else if (instance is SystemMutex mutex)
            {
                Threading.Mutex.Dispose(mutex);
            }
            else if (instance is System.Threading.ManualResetEventSlim manualResetEventSlim)
            {
                Threading.ManualResetEventSlim.Dispose(manualResetEventSlim);
            }
#if NET8_0_OR_GREATER
            else if (instance is System.Threading.Timer timer)
            {
                Threading.Timer.Dispose(timer);
            }
            else if (instance is System.Threading.CancellationTokenSource source)
            {
                Threading.CancellationTokenSource.Dispose(source);
            }
            else if (instance is System.Threading.CancellationTokenRegistration registration)
            {
                // A boxed copy is disposed. The registration is an immutable handle, so the copy denotes the same one.
                Threading.CancellationTokenRegistration.Dispose(ref registration);
            }
#endif
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

#if NET8_0_OR_GREATER
            if (instance is System.Threading.Timer timer)
            {
                return Threading.Timer.DisposeAsync(timer);
            }

            if (instance is System.Threading.CancellationTokenRegistration registration)
            {
                // A boxed copy is disposed. The registration is an immutable handle, so the copy denotes the same one.
                return Threading.CancellationTokenRegistration.DisposeAsync(ref registration);
            }
#endif

            return instance.DisposeAsync();
        }
    }
}
#pragma warning restore CS1591
#endif
