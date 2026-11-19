using System.Runtime.CompilerServices;

static class TestSetup
{
    // Several tests block pool threads on purpose (transports that ignore cancellation); on small CI machines that delays
    // the deadline timers other tests measure. More starting threads keeps those timing checks about the code, not the runner.
    [ModuleInitializer]
    internal static void Init() => ThreadPool.SetMinThreads(Math.Max(64, Environment.ProcessorCount * 4), 64);
}
