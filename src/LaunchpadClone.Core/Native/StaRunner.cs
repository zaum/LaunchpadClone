namespace LaunchpadClone.Core.Native;

/// <summary>
/// Runs COM-bound work (e.g. IShellLinkW) on a dedicated STA thread.
/// Single shared helper so discovery, icons and uninstall do not each
/// reinvent the thread bootstrap.
/// </summary>
public static class StaRunner
{
    public static T? Run<T>(Func<T?> func)
    {
        T? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null)
            throw captured;
        return result;
    }

    public static T? RunSilent<T>(Func<T?> func)
    {
        try
        {
            return Run(func);
        }
        catch
        {
            return default;
        }
    }
}
