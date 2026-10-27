# MidnightEssentials Updater

A small Windows app that installs and updates the **MidnightEssentials** World of Warcraft addon.
It finds your World of Warcraft folder, downloads the latest release from
[GitHub](https://github.com/NotOpti/MidnightEssentials/releases) and replaces the addon folders.
Your settings are kept — they live in the WTF folder, which the updater never touches.

## Download

Get `MidnightEssentialsUpdater.exe` from the [Releases](../../releases) page. No installation needed: just run it.
To remove it, delete the .exe. It also keeps a small settings file in `%APPDATA%\MidnightEssentials Updater`, which you can delete too.

## Build it yourself

On Windows, run `build.cmd`. It uses the C# compiler that comes with Windows, so nothing needs installing.
The app appears in `dist\`.

## Privacy policy

The updater only talks to GitHub (api.github.com, github.com and its download servers) to check for
and download addon releases. It sends no personal data and has no analytics or tracking.
It changes nothing on your system apart from the MidnightEssentials folders inside
`World of Warcraft\_retail_\Interface\AddOns` and its own settings file.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Opti](https://github.com/NotOpti)
- Approvers: [Opti](https://github.com/NotOpti)

Every release is built from this repository by GitHub Actions and signed only after manual approval.

## License

MIT — see [LICENSE](LICENSE). The bundled Inter font is under the SIL Open Font License — see [assets/OFL.txt](assets/OFL.txt).
