# Tasklabe

[日本語](README.md) | English

A task and project management app for Windows, using GitHub Projects as its data store.

**No Electron. No WebView. 100% native.**

Talks only to GitHub. No telemetry, no tracking.

> [!NOTE]
> Tasklabe is in beta. Screens and features may change in future updates.

## Features

- **My Tasks**: See your personal tasks and the team tasks assigned to you in one place
- **Plan**: Organize tasks in a hierarchy and manage assignees, schedules, estimates, and progress
- **Gantt chart**: See plan vs. actual at a glance, with progress lines and dependency arrows
- **Kanban**: Change status by drag and drop
- **Project status**: Track progress, schedule variance, overdue tasks, and workload per assignee
- **Keyboard first**: Shortcuts for common actions, plus a command palette

Tasks are stored as GitHub Issues and projects as GitHub Projects, so you can view and edit the same data on GitHub.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- x64 or ARM64
- A GitHub account (github.com)

> [!NOTE]
> The UI is currently available in Japanese only.

## Installation

Download the zip file from [Releases](https://github.com/fkawabata/Tasklabe/releases), extract it, and run `Tasklabe.exe`.

> [!IMPORTANT]
> Beta builds are not code-signed, so Windows may show "Windows protected your PC" on first launch. Click "More info", then "Run anyway".

## Usage

### Permissions

Tasklabe requests the following scopes. The access token is stored in Windows Credential Manager.

| Scope | Used for |
|---|---|
| `repo` | Reading and writing Issues (tasks), creating your personal repository |
| `project` | Creating, reading, and writing GitHub Projects |
| `read:org` | Listing the organizations you belong to |

### First-time setup

On your first sign-in, Tasklabe creates:

- A private repository for your personal tasks (default name: `tasklabe-personal`; you can choose an existing repository instead)
- A GitHub Project for your personal tasks (shown as "My Tasks" in the app)

### Using with a team

Team projects are created as GitHub Projects owned by an organization. Give each member write access to both the Project and the repository that holds its tasks.

On the authorization screen at sign-in, make sure to grant access to each organization you want to use. If the organization restricts OAuth App access, an owner must approve Tasklabe.

## Uninstall

Sign out from the app's settings, then delete the extracted folder and `%LOCALAPPDATA%\Tasklabe`. Your data on GitHub is not deleted.

## Build

Requires the .NET 10 SDK.

```powershell
dotnet build Tasklabe.slnx -c Release
dotnet run --project src/Tasklabe.App -c Release
```

## License

[MIT License](LICENSE)
