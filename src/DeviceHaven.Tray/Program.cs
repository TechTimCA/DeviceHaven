using DeviceHaven.Tray;

// One tray icon per signed-in user. Opening DeviceHaven again (from the Start
// menu) asks the running copy to show its status window instead.
using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeviceHaven.Tray.Show", out _);
using var single = new Mutex(true, @"Local\DeviceHaven.Tray", out var first);
if (!first)
{
    showRequested.Set();
    return;
}

new TrayApp().Run(showRequested);
