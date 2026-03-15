namespace VRCNext;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();
        new AppShell(args).Run();
    }
}
