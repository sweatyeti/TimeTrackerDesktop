using System.Threading;

namespace TimeTrackerDesktop.Platform.Windows;

/// <summary>
/// One-instance behaviour (plan Task 3.1, step 4).
///
/// The plan requires the transport and the Windows activation plumbing to stay behind this interface, so the
/// app can be written against "am I the first instance, and tell me when someone else launches" without
/// knowing whether that is a mutex and an event, a named pipe, or the Windows App SDK's activation
/// redirection. Those are genuinely swappable, and the choice belongs in the implementation.
/// </summary>
public interface ISingleInstanceService : IDisposable
{
    /// <summary>
    /// True when this process owns the instance. False means another instance is already running and has
    /// been asked to show itself, so the caller should exit.
    /// </summary>
    bool IsFirstInstance { get; }

    /// <summary>
    /// Raised in the first instance when a second launch asks it to show and focus. Never raised in a
    /// process that is not the first instance.
    /// </summary>
    event EventHandler? ShowRequested;
}

/// <summary>
/// A one-instance guard built from a named mutex and a named event (plan Task 3.1).
///
/// The mutex answers "am I first"; the event is how a later launch tells the first one to show itself. Both
/// are per-session (<c>Local\</c>), not machine-wide: two users logged in at once each get their own widget,
/// which is what a desktop app should do — the alternative is one user's launch surfacing another user's
/// window.
///
/// A named pipe would carry a payload and would be the right choice if a second launch ever had something to
/// say beyond "show yourself"; the event is the smaller thing that does the job today, and it cannot block
/// the first instance on a client that dies mid-message.
/// </summary>
public sealed class SingleInstanceService : ISingleInstanceService
{
    /// <summary>The per-session names. Stable strings: changing them silently allows two instances.</summary>
    private const string MutexName = @"Local\TimeTrackerDesktop.SingleInstance.Mutex";
    private const string ShowEventName = @"Local\TimeTrackerDesktop.SingleInstance.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showEvent;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread? _listener;

    private bool _disposed;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out _);

        // a thread-owned mutex: acquisition must happen on the thread that will release it, so this is done
        // synchronously here rather than on the listener thread
        IsFirstInstance = _mutex.WaitOne(0, exitContext: false);

        if(!IsFirstInstance)
        {
            // the second instance's only job: wake the first one, then let the caller exit
            if(EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? existing))
            {
                existing.Set();
                existing.Dispose();
            }

            return;
        }

        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);

        _listener = new Thread(Listen) { IsBackground = true, Name = "TimeTrackerDesktop.SingleInstance" };
        _listener.Start();
    }

    public bool IsFirstInstance { get; }

    public event EventHandler? ShowRequested;

    public void Dispose()
    {
        if(_disposed)
        {
            return;
        }

        _disposed = true;

        _cancellation.Cancel();
        _showEvent?.Set();                  // wake the listener so it can observe the cancellation

        if(_listener is not null && _listener.IsAlive)
        {
            _listener.Join(TimeSpan.FromSeconds(1));
        }

        _cancellation.Dispose();
        _showEvent?.Dispose();

        if(IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    /// <summary>Waits for a second launch, then reports it on the thread that raised the event.</summary>
    private void Listen()
    {
        if(_showEvent is null)
        {
            return;
        }

        while(!_cancellation.IsCancellationRequested)
        {
            if(_showEvent.WaitOne(TimeSpan.FromMilliseconds(250)))
            {
                if(_cancellation.IsCancellationRequested)
                {
                    return;
                }

                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
