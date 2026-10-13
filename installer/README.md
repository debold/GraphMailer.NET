# GraphMailer installer (WiX)

Builds a per-machine **MSI** plus a **setup.exe bootstrapper** that chains the .NET 10 Desktop
Runtime prerequisite. The MSI installs both apps to `C:\Program Files\GraphMailer\` (plus the
bundled HTML help under `…\GraphMailer\help\`), registers the **`GraphMailer`** Windows service
(auto-start, LocalSystem, restart-on-failure recovery actions), and adds Start-menu shortcuts for the (elevated) ConfigTool and the
**help start page** (`help\index.html`, opens in the default browser). Runtime data stays in
`C:\ProgramData\GraphMailer\` (created by the apps).

## Prerequisites

```powershell
dotnet tool install --global wix --version 5.*
```

The build script adds the required WiX extensions (`Util`, `BootstrapperApplications`, `Firewall`,
`Netfx`) on demand.

**PowerShell 7+** must be installed: the help renderer (`build-help.ps1`) needs the modern .NET
runtime, so `build-installer.ps1` and `build-help.ps1` run under `pwsh`. They may be started from
Windows PowerShell 5.1 — they automatically relaunch themselves under `pwsh 7` (and error clearly
if it is missing). `build-release.ps1` runs under either edition.

## Build

```powershell
.\build-installer.ps1            # MSI + bootstrapper  → C:\Build\GraphMailer.NET\Installers\<version>\
.\build-installer.ps1 -MsiOnly   # MSI only (no runtime download)
```

It first runs `build-release.ps1` (framework-dependent publish), then `build-help.ps1`
(renders the HTML help into `<release>\help`, stamped with the same version), then builds:

| Artifact | Ship this when… |
|---|---|
| `GraphMailerSetup-<ver>.exe` | **default** — downloads the .NET runtime on demand if missing (small) |
| `GraphMailerSetup-<ver>.exe` + `windowsdesktop-runtime-*.exe` (side by side) | fully offline install (runtime bundled as an external payload, matched by hash) |
| `GraphMailer-<ver>.msi` | the .NET 10 Desktop Runtime x64 is already guaranteed on the target (the MSI refuses to install without it) |

## Releasing

Releases are built by GitHub Actions, not on a workstation:

1. Run `.\tools\coverage.ps1 -IncludeLive` and review the gap list (release rule, see
   `docs/TEST_DOCUMENTATION.md` → *Code coverage*).
2. `.\tools\New-Release.ps1 -CoverageReviewed [-Summary "…"] [-Push]` turns the `## Unreleased`
   section of `CHANGELOG.md` into `## <version> — <date>`, commits `Release <version>` and tags
   `v<version>`. It works out the release commit's own build number and checks it after the
   commit, so the tag, the changelog and the binaries always agree.
3. Pushing the tag starts `.github/workflows/release.yml`:
   - checks that the tag matches the version the commit builds and that the changelog has the
     section,
   - builds, runs the unit and integration tests, builds the installer,
   - runs `tools/Test-Installer.ps1` on two fresh runners: a **fresh install** and an **upgrade**
     from the latest published release,
   - creates a **draft** GitHub release with the setup, the MSI, release notes from the changelog
     section and build-provenance attestations (`gh attestation verify <file> --repo debold/GraphMailer.NET`).
4. Review the draft on GitHub (title, notes) and publish it.
5. Bump `<Version>` in `src/Directory.Build.props` for the next development cycle.

*Actions → Release → Run workflow* on `main` runs step 3 without creating a release: a dry run
of the build and the installer tests.

### What the installer tests check

`tools/Test-Installer.ps1` installs, exercises and uninstalls GraphMailer. It changes the machine
(service, certificate, firewall rule, `C:\ProgramData\GraphMailer`), so it runs on GitHub runners;
on a test VM it needs `-AllowMachineChanges`.

- **Install**: exactly one visible and one hidden *Programs and Features* entry with the expected
  version; `GraphMailer.exe` has the expected FileVersion; the service is registered as
  LocalSystem with automatic start and is running; ConfigTool, help, both Start-menu shortcuts and
  the firewall rule are present; first-run provisioning created `graphmailer.json` and the
  self-signed `GraphMailer SMTP` certificate; ports 25, 465 and 587 listen.
- **Service**: a message sent unauthenticated from localhost on port 25 is accepted and stored;
  implicit TLS on port 465 negotiates TLS 1.2+ and presents the self-signed certificate.
- **Upgrade** (`-PreviousSetup`): before the upgrade the old version gets an SMTP banner marker,
  an SMTP user whose password it captures and stores as `ENC[…]`, and a queued message. After
  installing over it: the installation was replaced, not duplicated; the banner marker is served
  (configuration kept and read by the new version); the captured password still authenticates and
  a wrong one is rejected (the Data Protection key ring survived); the queued message is still
  there.
- **Uninstall**: service, program files, shortcuts, firewall rule and *Programs and Features*
  entries are gone; `C:\ProgramData\GraphMailer` (configuration, keys, mail) is kept.

## Install / uninstall

### Interactive
```
GraphMailerSetup-<ver>.exe
```

### Silent
```powershell
# Bootstrapper (installs runtime prereq if needed, then the MSI)
GraphMailerSetup-<ver>.exe /quiet  /norestart
GraphMailerSetup-<ver>.exe /uninstall /quiet /norestart
GraphMailerSetup-<ver>.exe /passive /norestart          # progress bar, no prompts
GraphMailerSetup-<ver>.exe /quiet /log C:\Temp\gm.log    # write a log

# MSI directly (no runtime handling)
msiexec /i GraphMailer-<ver>.msi /qn /norestart
msiexec /x GraphMailer-<ver>.msi /qn /norestart
msiexec /i GraphMailer-<ver>.msi /qn /l*v C:\Temp\gm-msi.log
```

Upgrades are major upgrades: installing a newer version removes the old one automatically
(uses a stable `UpgradeCode`). The service is stopped/removed on uninstall and started on install.

## Files

- `Package.wxs` — the MSI (files, service install/control, shortcut, major-upgrade).
- `Bundle.wxs` — the Burn bootstrapper (runtime prereq detection + chain).
- `..\build-installer.ps1` — orchestrates publish → MSI → bootstrapper, resolves & downloads
  the runtime, and stamps the version from `src\Directory.Build.props`.

## Notes

- **Firewall**: the MSI registers an inbound exception bound to `GraphMailer.exe` (program-based,
  no fixed port) so any configured SMTP listener port is reachable without manual rules; it is
  removed on uninstall.
- **Launch on finish**: an interactive bootstrapper install shows a "Launch" button that starts
  the ConfigTool (`LaunchTarget`); suppressed under `/quiet` and `/passive`.
- **Service account**: `LocalSystem`. It can read the registry Data Protection key ring
  (`HKLM\SOFTWARE\GraphMailer\DataProtection`) and bind the SMTP ports.
- **Service recovery**: the MSI configures restart-on-failure (`util:ServiceConfig` — restart
  60 s after a crash, up to three times, failure counter resets daily), matching the sc.exe
  failure actions set by the CLI install (`GraphMailer.exe --install`).
- **Runtime detection**: the bootstrapper skips the runtime install when the shipped
  `Microsoft.WindowsDesktop.App\<version>` folder is already present; otherwise it installs it
  silently and leaves it in place on uninstall (`Permanent`).
- **Payload pruning**: `build-installer.ps1` deletes debug symbols (`*.pdb`) from the publish
  before packaging, so they are not shipped. Everything else in the publish is required at
  runtime (`*.deps.json` / `*.runtimeconfig.json`, `appsettings.json`, and the flattened
  win-x64 native libs — there are no dead non-win-x64 runtimes). `build-release.ps1` keeps the
  symbols for manual deployments.
- Validate a real silent install/uninstall on a clean VM before shipping — building the
  installer does not install it.
