using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RTFlushRepro;

/// <summary>
/// Reproduces the NVIDIA/Wayland cross-context fence deadlock in Clyde's
/// secondary-window blit path.
/// </summary>
public sealed class EntryPoint : GameClient
{
    private readonly List<OSWindow> _windows = new();

    public override void PreInit()
    {
        var config = IoCManager.Resolve<IConfigurationManager>();

        // Does not seem to matter, it can be set to either value.
        config.OverrideDefault(CVars.DisplayThreadWindowBlit, false);
        // Also does not seem to matter.
        config.OverrideDefault(CVars.DisplayThreadUnlockBeforeSwap, true);
    }

    public override void Init()
    {
        var componentFactory = IoCManager.Resolve<IComponentFactory>();
        componentFactory.DoAutoRegistrations();
        componentFactory.GenerateNetIds();
    }

    public override void PostInit()
    {
        // This stuff is not necessary for the repro, but it shows that the
        // repro is working.
        var clyde = IoCManager.Resolve<IClyde>();
        IoCManager.Resolve<IUserInterfaceManager>().OnPostDrawUIRoot +=
            args => DrawHeartbeat(clyde, args);

        // Two secondary windows are the smallest configuration observed to
        // reproduce the multi-context fence deadlock. One still exercises the
        // secondary blit path, but does not trigger the deadlock here.
        for (var i = 0; i < 2; i++)
        {
            var window = new OSWindow();
            window.Create();
            _windows.Add(window);
        }
    }

    private static void DrawHeartbeat(IClyde clyde, PostDrawUIRootEventArgs args)
    {
        if (args.Root.Window != clyde.MainWindow)
            return;

        var color = Environment.TickCount64 / 500 % 2 == 0
            ? Color.Lime
            : Color.Red;
        args.DrawingHandle.DrawRect(
            UIBox2.FromDimensions(Vector2.Zero, new Vector2(32, 32)), color);
    }
}
