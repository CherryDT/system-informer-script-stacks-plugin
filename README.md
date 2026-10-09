# Script Stacks for System Informer

Capture JavaScript, Python and Java thread stacks from extra tabs in the normal Windows process properties window. No changes to System Informer itself are required. Renamed executables and embedded runtimes are detected from loaded libraries, version resources and Node's registered debug handler, rather than the executable name alone.

> ⚠️ **Loading this plugin:** System Informer's driver module (KPH) does not trust this plugin yet. Its own signing policy means ordinary code signing would not be enough. When KPH is enabled, loading this plugin requires disabling image-load protection, which also reduces the kernel-driver access available to System Informer. Setup explains this tradeoff and asks explicitly. These shared host options affect other third-party plugins too.

## Install

> **TL;DR:** Close System Informer, run `ScriptStacks-0.1.0-setup-x64.exe`, select its folder, and review the plugin-loading options. KPH needs the untrusted-plugin option because it does not trust this DLL. Uninstall can optionally undo both loading settings.

The installer detects System Informer's location and lets you browse to another installation. It registers **Script Stacks for System Informer** in Installed apps. If the selected instance is running, setup offers Retry or Cancel; it does not terminate it. Already-correct settings are reported without asking to change them again. When KPH is disabled, its image-protection option is not needed.

A portable ZIP is also provided. Copy `ScriptStacks.dll` and the `ScriptStacks` directory into System Informer's `plugins` directory. Enable plugins and third-party discovery (`EnablePlugins=1`, `EnableDefaultSafePlugins=0`). If using KPH, review the tradeoff above before setting `KsiDisableImageLoadProtection=1`. Restart System Informer. Use a recent x64 build; ARM64 System Informer and x86 hosts are not supported by this package.

The capture helper is self-contained: end users do not need to install .NET. The helper runs separately from System Informer and never loads a managed runtime into the host.

## Use

Open a process's normal Properties window, select **JS Stacks**, **Python Stacks**, or **Java Stacks**, and choose **Capture stacks**. Captures are explicit, not continuous. The page shows progress and supports cancellation, search, copy and saving text. A process may have more than one runtime tab.

When a required external tool is missing, capture offers to install it and retry. Installation is optional and uses a private folder at `%LOCALAPPDATA%\SystemInformerScriptStacks\tools`. It does not change PATH, replace an application's runtime, or require a system-wide Python/JDK installation. Downloads are pinned to release URLs and SHA-256 hashes and verified before extraction. Existing suitable tools are reused. JVMs without a supported matching download receive instructions instead of an incompatible tool.

### JavaScript / Node / Electron

The plugin connects only to loopback inspector listeners owned by the selected PID and verifies `process.pid` through the protocol. If no inspector is enabled, it asks to temporarily enable one through Node's registered Windows debug handler. The process is identified by PID and creation time, so renaming `node.exe` does not prevent capture. No code is copied into the target.

A capture briefly pauses and resumes JavaScript in the main isolate and available Node worker isolates. Worker discovery is feature-probed because the NodeWorker protocol varies across runtime versions. A lone `processTimers` frame is retried for up to 200 ms, with 10 ms between attempts. Native frames, historical asynchronous tasks and a simultaneous snapshot of every isolate are not included.

An inspector that was already running is left running. By default, an inspector activated by the plugin is closed afterward when the plugin can confirm its connection is the only one; if another debugger connects, it is left enabled with an explanation. The Options menu can change this behavior and remember activation consent. Closing the listener can race with other debuggers, so avoid simultaneous debugger attachment during capture.

Electron's main process works when its embedded Node runtime exposes the standard inspector facilities. Packaged builds may disable these with Electron fuses, and Chromium renderer processes use a different debugging protocol. These cases are reported explicitly; the plugin does not bypass disabled debugging or restart the application with different flags. For supported main processes without dynamic activation, start the application with `--inspect=127.0.0.1:0` and retry. Detection of an Electron executable is not a guarantee that every process it creates allows stack capture.

### Python

Python capture uses **py-spy 0.4.2** to read all discoverable CPython thread stacks. The private installation extracts the standalone executable from the official Windows wheel; Python and pip are not needed for installation. Capture uses nonblocking reads so cancellation cannot leave the target suspended; changing frames can therefore be incomplete.

Windows x64 CPython is supported, subject to py-spy's runtime-version support. Renamed launchers and embedded CPython can work when their runtime state is accessible. Windows 32-bit Python, PyPy and fully transformed/native-compiled programs are not supported by this backend. Access-denied results explain when matching elevation is needed.

### Java

Java capture uses `jcmd <pid> Thread.print -l`, including thread names, states and lock information supplied by the JVM. The plugin first looks for the JDK belonging to the loaded JVM, then suitable configured or privately installed tools. It checks architecture and Java major version before launching a tool.

Private Eclipse Temurin downloads are provided for Java 8, 11 and 17 on x86/x64 and Java 21, 25 and 26 on x64. Other versions require a matching JDK installed separately. The target JVM may prohibit attaching (`-XX:+DisableAttachMechanism`), require matching access rights, or use an unsupported VM implementation. Repackaging an executable alone does not prevent detection of its loaded JVM.

### .NET

No duplicate .NET tab is added. System Informer's bundled **DotNetTools** already supplies managed-frame information in its ordinary thread stack view. Enable that plugin for .NET inspection.

## Settings and limitations

Preferences live in System Informer's settings file under `DavidTrapp.ScriptStacks.*`:

| Setting | Default | Meaning |
|---|---:|---|
| `AlwaysShowAllTabs` | 0 | Show all three tabs even when detection cannot identify a runtime; reopen the properties window after changing it. Available through Advanced settings and a stack page's Options menu. |
| `UseNodeInspectorWithoutAsking` | 0 | Remember permission to activate a missing Node inspector on capture. |
| `CloseOwnedInspector` | 1 | Try to close an inspector activated for the current capture, while preserving pre-existing inspectors and other clients. |

Protected processes, another account's processes and sandboxed applications may deny inspection. Elevate System Informer only when the target's access rules require it. PID reuse is checked using process creation time before capture, installation and tool launch. Capture jobs have output limits and timeouts; closing a page cancels its helper work without terminating the inspected process. No stack collection runs in the background.

Runtime detection is evidence-based, not universal: an application can strip version information, hide its runtime, or omit supported diagnostic facilities. `AlwaysShowAllTabs` lets you attempt the relevant backend anyway. Opening properties before a runtime loads may require reopening the window.

## Uninstall

Use Installed apps. The uninstaller removes this plugin and its helper payload. Removing privately downloaded tools is optional. Restoring the third-party-loading policy and KPH image-load protection is also optional, default off, and only offered when applicable. Consider other installed plugins before resetting shared host settings. To restore KPH image-load protection manually, set `KsiDisableImageLoadProtection=0` and restart System Informer.

## Build

Install Visual Studio 2022 with Desktop development with C++, a Windows SDK, the .NET 9 SDK, and Inno Setup 6. In PowerShell:

```powershell
.\scripts\build.ps1
.\scripts\package.ps1
```

The build pins the System Informer SDK source revision and generates its public headers/import library. It does not require that exact four-part host version at runtime. Compatibility still depends on the plugin interfaces used by the host. `-SkipSdk` reuses an already-prepared SDK; `-SkipHelper` rebuilds only the native DLL. Build outputs and downloaded SDK files are ignored by Git.

The native DLL uses Control Flow Guard. The C bridge contains System Informer-specific interfaces; C++ owns detection, page presentation and the asynchronous helper transport. The C# helper owns runtime protocols, process identity checks and verified tool installation. Third-party license notices are included in release packages.

MIT license — David Trapp / Trapp Innovations.
