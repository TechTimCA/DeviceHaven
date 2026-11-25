<p align="center">
  <img src="assets/brand/DeviceHaven-Logo.png" alt="DeviceHaven Endpoint Management" width="480">
</p>

<p align="center"><em>Every Device. One Platform.</em></p>

DeviceHaven is free, self-hosted endpoint management for Windows. Install it like
any other program, and manage your PCs from a clean web dashboard. Everything stays
on your own network: there is no cloud service, no account and no tracking.

> **Status: early development (v0.1, stage 1 of 4).** Not ready for production use yet.

## What works today

- **Installers**: `DeviceHaven-Server.msi` and `DeviceHaven-Agent.msi`, with silent switches for Group Policy, Intune or any RMM.
- **Web console**: devices with online status, full inventory per device, enrolment codes with ready-to-copy install commands, agent settings and the audit log. Light and dark mode.
- **Server** (Windows service): HTTPS with its own certificate, made on first start and trusted on the server so the console opens without a warning.
- **Enrolment codes**: for one PC or a whole batch (up to 1,000), expiring after 1–168 hours, stored only as hashes.
- **Agent** (Windows service): enrols once, then checks in on the schedule you set. Windows restarts it automatically if it ever stops.
- **Inventory**: hardware, serial number, Windows version and build, disks and BitLocker status, logged-on user, and installed programs. Only changes are sent.
- **Audit log** that can only be added to, never edited or deleted.

## Install

### 1. Server

Run **DeviceHaven-Server.msi** on the PC or server that will run DeviceHaven, then open
**DeviceHaven Console** from the Start menu.

Silent install:

```
msiexec /i DeviceHaven-Server.msi /qn
```

| Property | Default | What it does |
|---|---|---|
| `PORT` | `8443` | HTTPS port for the console and agents. Opened in Windows Firewall for domain and private networks only. |
| `PUBLICURL` | `https://<this PC's name>:8443` | The address agents use, if they reach the server by another name. |

### 2. Agents

In the console, choose **Add devices**, pick how many PCs, and create a code. Then on each PC
either run **DeviceHaven-Agent.msi** and enter the details it asks for, or install silently:

```
msiexec /i DeviceHaven-Agent.msi /qn SERVERURL="https://devicehaven:8443" FINGERPRINT="<from the console>" ENROLLCODE="<code>"
```

| Property | Needed | What it does |
|---|---|---|
| `SERVERURL` | First install | The server's address, shown in the console. |
| `FINGERPRINT` | First install | The server's certificate fingerprint. The agent will only ever trust this server. |
| `ENROLLCODE` | First install | A code from the console. Never written to install logs. |

Upgrades need no properties: `msiexec /i DeviceHaven-Agent.msi /qn` keeps the existing enrolment.
Uninstall with `msiexec /x DeviceHaven-Agent.msi /qn`.

Download the latest MSIs from the [Releases page](https://github.com/TechTimCA/DeviceHaven/releases). Every build of `main` that passes its install test is published there as a preview.

## Roadmap

DeviceHaven 1.0 is planned for **December 2027**, with one stage per quarter. See the full
[2027 roadmap](ROADMAP.md) for every feature, target month and gate.

| When | Release | What you get |
|---|---|---|
| Now | 0.1 previews | ✅ Installers, console, secure enrolment and inventory |
| Q1 2027 | 0.2 public beta | Admin sign-in with two-step verification, tray app, lighter agent, signed MSIs |
| Q2 2027 | 0.5 | Software deployment (winget catalog), Windows patching, scripts and reports |
| Q3 2027 | 0.8 | Policies, BitLocker and admin passwords, remote tools, alerts, security hardening |
| Q4 2027 | 1.0 | Imaging (USB and network boot, zero-touch), macOS and Linux agent previews |

The protocol, server and agent core are already cross-platform .NET. Only the parts that
talk to the operating system (inventory, secret storage, service hosting and installers) are
Windows-specific, and they sit behind interfaces so macOS and Linux versions can be added
alongside them.

## How it connects

```
 Tray app  ──(local only)──  Agent service  ──(outbound HTTPS, pinned)──▶  DeviceHaven Server  ◀── your browser
 (no admin rights)           (runs as SYSTEM)                              (Windows service + database)
```

- The agent always connects **out** to the server, so you don't need to open firewall ports on your PCs.
- The agent only trusts the one server certificate it was installed with (its SHA-256 fingerprint), so a fake server on the network can't take over enrolled PCs.
- See [docs/SECURITY.md](docs/SECURITY.md) for the full security design.

## Requirements

| | Needs |
|---|---|
| Server | 64-bit Windows 10/11 or Windows Server 2019+. Nothing else: .NET and the database are bundled. |
| Agent | 64-bit Windows 10/11. Nothing else. |
| Imaging (stage 4) | Microsoft's free Windows ADK and WinPE add-on. |

Until admin sign-in is built, the console only works when opened on the server itself.

## Building from source

Needs the .NET 10 SDK. The installers also need Windows (WiX Toolset 5).

```powershell
dotnet build DeviceHaven.slnx
dotnet test DeviceHaven.slnx
```

## Project layout

```
src/DeviceHaven.Shared   Protocol messages and certificate pinning, shared by server and agent
src/DeviceHaven.Server   ASP.NET Core server, SQLite database, admin and agent APIs
src/DeviceHaven.Agent    Windows service: enrolment, check-in, inventory
tests/                   Security and end-to-end tests (real agent loop against real server)
installer/               WiX MSI projects for the server and agent
assets/                  Brand artwork, Windows icons and installer images
```

## Licence

To be decided before the first public release.
