# TuiDwm.Plugins.SysInfo — System Info Plugin

## What this is
Displays process/system metrics. Currently reads the compositor's own process via Process.GetCurrentProcess().

## Known issue
Displays compositor process stats (RAM, threads) not system-wide metrics, despite saying "SYSTEM OVERVIEW".
May be intentional for dev diagnostics. If system-wide metrics are wanted, use PerformanceCounter or WMI.
