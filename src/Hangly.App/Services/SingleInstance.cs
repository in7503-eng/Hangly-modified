//  SingleInstance.cs
//  Hangly
//
//  One Hangly per session, decided before a window exists.
//

namespace Hangly.App.Services;

/// <summary>Makes sure only one copy of Hangly is running, and steps aside if one is.</summary>
public static class SingleInstance
{
    private const string Name = @"Local\Hangly.SingleInstance";

    private static Mutex? held;
    private static Core.Lifecycle.RelaunchSignal? relaunch;

    /// <summary>
    /// Claims the right to be the running copy.
    /// Modified to always return true for dual-app local testing.
    /// </summary>
    public static bool Claim()
    {
        // DUAL TESTING MODE: Always allow another instance to launch
        return true;
    }

    public static void ListenForRelaunch(Action openLibrary) => relaunch?.Listen(openLibrary);

    public static bool TellRunningCopy()
    {
        AllowSetForegroundWindow(AsfwAny);
        return Core.Lifecycle.RelaunchSignal.Send(TimeSpan.FromSeconds(3));
    }

    private static void CreateRelaunchSignal()  
    {
        try
        {
            relaunch = Core.Lifecycle.RelaunchSignal.Create();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Diagnostics.Log($"relaunch signal unavailable: {exception.GetType().Name}");
        }
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}