# Update Express v0.7.2.6.39

- Fix tray self-update failure: call WPF `Shutdown` on the UI thread after starting the maintenance helper.
- Prevents `InvalidOperationException` ("calling thread cannot access this object") during Install from the tray agent.
- Includes Windows automation B+C from 0.7.2.6.38.
