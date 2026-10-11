namespace Sovereign.Desktop.Connector;

/// <summary>Runs UI work inline or on a dispatcher queue, and returns if that queue does not finish within the budget.</summary>
public static class UiDelivery
{
    public static void Invoke(bool onCallerThread, Action work, Action<Action> enqueue, TimeSpan budget)
    {
        if (onCallerThread)
        {
            work();
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        enqueue(() =>
        {
            try
            {
                work();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        var finished = false;
        try
        {
            finished = done.Task.Wait(budget);
        }
        catch (AggregateException)
        {
            finished = done.Task.IsCompleted;
        }

        if (!finished) return;
        done.Task.GetAwaiter().GetResult();
    }
}
