namespace MultiMon.Control.Shared;

/// <summary>
/// The view-model's only route back to the UI thread. <see cref="IPerformanceController"/> raises
/// StateChanged / CommandFailed on its worker thread (the V0087 rule: the UI thread never blocks on
/// native work), and probe results arrive on the thread pool — both must land on the UI thread before
/// they touch a binding. Keeping that behind an interface is what makes <see cref="MainViewModel"/>
/// platform-free: WPF supplies a Dispatcher-backed implementation, Avalonia a Dispatcher.UIThread one.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Queue <paramref name="action"/> on the UI thread and return immediately. Never blocks.</summary>
    void Post(Action action);
}

/// <summary>Runs everything inline — for headless hosts (tests, the dev autoperform switch) with no UI thread.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
}
