Shoko MPC-HC
============

A Shoko Server plugin that adds a Play in MPC-HC button beside episodes in
the existing Web UI. The player runs on your Windows PC and can start from
closed. No browser extension or playlist download is required for playback.

REQUIREMENTS
Shoko 6.0.0-dev.550, Shoko.Abstractions 6.0.0-alpha.112, Web UI 2.6.0-dev.153.
Windows with .NET Framework 4.x and MPC-HC. Shoko's 6.0 plugin API is still
changing: matching the major version alone does not guarantee compatibility.

INSTALL THE SERVER PLUGIN
In Shoko Settings > Plugin Management > Repositories, add this manifest URL:
https://raw.githubusercontent.com/alekso56/Shoko-MPC-HC/feature/shoko-mpc-playback/manifest.json
Then find Shoko MPC-HC under Browse, install, and restart Shoko.
Shoko downloads the versioned release ZIP from GitHub and verifies its SHA256.
The implementation branch awaits human review before merging into main.

SET UP WINDOWS ONCE
Open MPC-HC setup from Shoko's plugin pages, download Windows Setup, extract
the ZIP, and run Install.cmd. Enter the same Shoko base URL you use in the
browser. Setup discovers common MPC-HC locations or asks you to select it.
For unattended installation, use:
  powershell -NoProfile -File Install.ps1 -ServerUrl http://shoko-server:8111
An optional -PlayerPath argument selects your MPC-HC executable.
Setup installs into LOCALAPPDATA/ShokoMpcHandler and registers shoko-mpc links
for the current Windows user. No administrator privileges or service needed.

PLAY
Refresh the episode page. Click the MPC-HC button beside its AniDB link.
Accept the browser's external-application prompt. Some browsers need a second
gesture: use Open MPC-HC in the notification. Browser prompts cannot be
disabled by the server plugin. The Windows handler launches MPC-HC directly.
Multi-part episodes pass all parts to the player's playlist.
From version 0.1.2, the player receives a stream URL ending in the original
video filename, so its title bar can display the filename instead of a token.
Existing installations should update the server plugin and run Windows Setup
once more. Older Windows handlers remain compatible but keep token-only URLs.

SECURITY AND LIMITATIONS
The server uses your existing Shoko browser session for launch authorization.
The handler receives only an opaque, single-use ticket valid for 90 seconds.
Video capabilities are scoped to the selected files and user for 12 hours.
The server rechecks file permissions on every stream request. Restarting
Shoko invalidates all outstanding sessions. No permanent API key leaves the
Web UI. Configure HTTPS when using Shoko over an untrusted network.
The handler pins the server address and rejects arbitrary URLs, executables,
redirects, extra protocol parameters, and shell arguments. It never invokes
a shell. Keep the setup folder and installed settings writable only by you.
This release streams original video files, with byte-range seeking. It does
not transcode, update watched status, restore resume position, or transfer
separate subtitle files. Embedded subtitles remain in the original video.
The web adapter uses the visible episode's AniDB link; it does not inspect
React internals or alter Shoko's installed Web UI files. Future UI changes
may require an adapter update. The current middleware targets /webui routes.

REMOVE
Run Uninstall.cmd to remove the Windows protocol and handler. It refuses to
overwrite or remove a protocol owned by another program. Disable/uninstall
the plugin through Shoko and restart the server to remove the episode buttons.

BUILD AND TEST
On Windows, install .NET SDK 10. Run ./build.ps1 from PowerShell. It compiles
the Windows handler, embeds setup, builds the plugin, runs backend/protocol
tests and packages artifacts. It updates manifest.json with the archive hash.
For DOM tests, run npm ci in Tests and then npm test.
Do not commit bin, obj, node_modules, artifacts, local settings or credentials.
To publish another version, update the project, dashboard, build script and
manifest release metadata together, then upload the exact generated ZIP.

Apache License 2.0. See LICENSE.
