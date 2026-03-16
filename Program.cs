using VRCNext.Services;

namespace VRCNext;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        CrashHandler.Register();
        Velopack.VelopackApp.Build().Run();
        new AppShell(args).Run();
    }
}
