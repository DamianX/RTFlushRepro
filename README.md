# RobustToolbox secondary-window fence repro

This is a minimal RobustToolbox client that creates two empty hidden
secondary SDL/OpenGL windows in order to reproduce a deadlock involving
Clyde's secondary-window blit path and the cross-context GL fence wait
when running on NVIDIA/Wayland.

`RobustToolbox/` is pinned to the RT revision currently used by
upstream OpenDream:

```text
v277.0.0
08a3d120b7029d03e60b44b23fed2b2659ed3224
```

## Requirements

- .NET 10 SDK
- Linux running Wayland and an NVIDIA OpenGL driver to reproduce the bug

There's no reason this wouldn't run on other platforms but the bug has only been
observed with that combination.

## Clone and run

Clone the repository with its submodule:

```sh
git clone --recurse-submodules https://github.com/DamianX/RTFlushRepro
cd RTFlushRepro
dotnet run --project RTFlushRepro.csproj
```

The client opens the main window and creates two empty hidden secondary
windows. On an affected NVIDIA/Wayland setup, the unpatched RT can stop
progressing when a secondary blit thread waits for the main-context fence. Two
live secondary windows are the minimum observed here; one does not reproduce
the deadlock.