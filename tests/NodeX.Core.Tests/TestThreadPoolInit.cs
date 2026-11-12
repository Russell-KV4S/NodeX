using System.Runtime.CompilerServices;
using System.Threading;

namespace NodeX.Core.Tests;

internal static class TestThreadPoolInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Prevent ThreadPool 500 ms hill-climbing stalls when xUnit runs dozens of socket/timer tests
        // concurrently on 2-vCPU CI runners.
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 64), Math.Max(completionPortThreads, 64));
    }
}
