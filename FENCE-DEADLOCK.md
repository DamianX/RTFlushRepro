# NVIDIA/Wayland secondary-window fence deadlock

## Minimal reproducer

On a desktop OpenGL NVIDIA/Wayland setup, two empty secondary `OSWindow`s are
sufficient to reproduce the problem:

```csharp
private readonly List<OSWindow> _windows = new();

public override void PostInit()
{
    for (var i = 0; i < 2; i++)
    {
        var window = new OSWindow();
        window.Create();
        _windows.Add(window);
    }
}
```

The windows do not need to be visible, contain controls, or use WebView. One
secondary window exercises the blit path, but two are the smallest configuration
observed to trigger the deadlock.

## What Clyde does

For each secondary window, Clyde creates an offscreen render target and a GL
context that shares objects with the main context. The main OpenGL context
renders the window's contents into that target. At the end of rendering, it
inserts a fence:

```csharp
private void FenceRenderTarget(RenderTargetBase rt)
{
    if (!_hasGLFenceSync || !rt.MakeGLFence)
        return;

    if (rt.LastGLSync != 0)
        GL.DeleteSync(rt.LastGLSync);

    rt.LastGLSync = GL.FenceSync(
        SyncCondition.SyncGpuCommandsComplete,
        WaitSyncFlags.None);
}
```

The fence is created after the render queue has emitted the target's drawing
commands:

```csharp
FlushRenderQueue();
RenderWindowContents();
FlushRenderQueue();
FenceRenderTarget(target);
```

At the end of the frame, Clyde presents secondary windows by using each
window's separate GL context. It waits for the main-context fence and then
blits the render-target texture into the native window framebuffer:

```csharp
foreach (var window in secondaryWindows)
{
    MakeContextCurrent(window.Context);

    GL.WaitSync(
        window.RenderTarget.LastGLSync,
        WaitSyncFlags.None,
        GL_TIMEOUT_IGNORED);

    DrawFullscreenQuad(window.RenderTarget.Texture);
    SwapBuffers(window);
}
```

With threaded blitting, each secondary window has its own `WinBlitThread`; the
main thread wakes and waits for each worker in turn. By default, the worker sets
its `BlitDoneEvent` after `SwapBuffers()`. `DisplayThreadUnlockBeforeSwap` can
move the signal before the swap on later frames, but the per-window field starts
false and is not updated until after the first blit wait, so it does not avoid
the startup deadlock in this reproducer. With threaded blitting disabled, the
main thread switches contexts and runs the same wait/blit/swap code
synchronously.

## Why the deadlock occurs

`FenceSync()` inserts a fence into the main context's command stream. It does
not necessarily submit that command stream to the graphics pipeline.
`FlushRenderQueue()` only emits Clyde's queued rendering calls to OpenGL; it is
not the OpenGL `GL.Flush()` call.

The ordering can therefore become:

```text
Main context:
    render secondary target
    FenceSync()

Main thread:
    wake secondary blit
    wait for BlitDoneEvent

Secondary context:
    WaitSync(main-context fence)
    draw and SwapBuffers()
```

`GL.WaitSync()` is a server-side wait: the call normally returns to the CPU
after queuing the dependency, but later commands in that secondary context
cannot execute until the fence is signaled. In practice the thread commonly
blocks when `SwapBuffers()` forces that queue to make progress, rather than
inside the `GL.WaitSync()` call itself.

The fence cannot become signaled until the main context submits it. But the main
window's later `SwapBuffers()` would normally flush the main context only after
the secondary blit path returns. With synchronous blitting, the main thread is
itself stuck in the secondary swap. With the default threaded behavior, it is
waiting for `BlitDoneEvent` while the worker is stuck in that swap. This creates
the circular wait:

```text
main thread waits for the secondary blit/swap to return
secondary command stream waits for the main-context fence
fence is not submitted
main context would flush only after the wait completes
```

Some drivers flush pending commands eagerly or as a side effect of internal
queue management, so the bug may not appear there. NVIDIA under Wayland can
defer submission long enough for the wait to hang indefinitely.

## Why `GL.Flush()` is the correct fix

Flush the creating context after all secondary-window fences have been inserted
and before either waking workers or switching to a secondary context:

```csharp
private void BlitSecondaryWindows()
{
    // The single-main-window early return is above this in the real method.
    if (!Clyde._hasGLFenceSync &&
        Clyde._cfg.GetCVar(CVars.DisplayForceSyncWindows))
    {
        GL.Finish();
    }
    else if (Clyde._hasGLFenceSync)
    {
        GL.Flush();
    }

    // Wake workers or switch to each secondary context and blit.
    // ...
}
```

In the actual patch this logic is at the start of `BlitSecondaryWindows()`. The
main context is still current there, and both the threaded and synchronous
paths occur afterward, so one flush covers all fences produced earlier in the
frame.

`GL.Flush()` submits previously issued commands and returns without waiting for
the GPU to finish them. The secondary context's `WaitSync()` then provides the
server-side ordering (the implementation may enforce that wait on the CPU or
the GPU):

```text
main context:
    render
    FenceSync()
    GL.Flush()       // submit the fence command

secondary context:
    WaitSync()       // wait for rendering to complete
    blit
```

`GL.Finish()` would also avoid the deadlock, but it blocks the CPU until all
previous main-context commands complete. That defeats the purpose of the
asynchronous cross-context fence and can stall every frame. Clyde uses it here
only when fence synchronization is unavailable and the
`DisplayForceSyncWindows` fallback is enabled (it is enabled by default).

[`ARB_sync` §5.2.2](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_sync.txt)
explicitly warns that a wait may hang when its fence has not been flushed, and
that an application waiting on a fence issued by another context must ensure
the creating context flushes it. `GL_SYNC_FLUSH_COMMANDS_BIT` does not replace
this fix: it is only accepted by `GL.ClientWaitSync()`, and a client wait in the
secondary context flushes that calling context, not the main context that
created the fence. `GL.WaitSync()` itself requires zero flags.

The explicit `GL.Flush()` therefore supplies the missing cross-context command
submission without forcing the main thread to wait for GPU completion.
