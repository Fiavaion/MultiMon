using Avalonia.Threading;
using MultiMon.Control.Shared;

namespace MultiMon.Control.Mac;

/// <summary>
/// <see cref="IUiDispatcher"/> over Avalonia's UI thread. The controller raises StateChanged /
/// CommandFailed on its own worker (the V0087 rule), so every handler that touches a binding comes
/// through here. <see cref="Post"/> never blocks — the UI thread must never wait on the controller.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
