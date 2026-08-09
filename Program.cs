using System;
using Robust.Client;

namespace RTFlushRepro;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ContentStart.StartLibrary(args, new GameControllerOptions
        {
            Sandboxing = false
        });
    }
}
