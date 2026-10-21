using System.Runtime.CompilerServices;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

// Several tests deliberately block thread-pool threads (the Blocking*Gate helpers) while xUnit runs test classes
// in parallel. With the default minimum of one worker per core, those blocks can starve the work another test is
// waiting for until thread injection catches up, which exceeded the 5 s gate safety timeout on a 4-core machine.
// A higher minimum removes that starvation without changing what any test asserts.
internal static class ThreadPoolHeadroom
{
#pragma warning disable CA2255 // Test assembly setup, not a library consumed by others.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 64), completionPortThreads);
    }
}
