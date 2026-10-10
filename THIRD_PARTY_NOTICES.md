# Third-party notices

Zarp's own code is licensed under the [MIT License](LICENSE).

`Zarp.exe` also embeds the third-party software listed below. zapret2 is extracted on first run to `%LOCALAPPDATA%\Zarp\zapret2` and started as a separate process (`winws2.exe`). sing-box is extracted when you use your own server to `%LOCALAPPDATA%\Zarp\singbox` and also started as a separate process. Zarp does not link against any of it. All binaries are redistributed unmodified, exactly as published in the [zapret2 releases](https://github.com/bol-van/zapret2/releases) and the [sing-box releases](https://github.com/SagerNet/sing-box/releases).

| Component | Files | License | Source code |
|---|---|---|---|
| [zapret2](https://github.com/bol-van/zapret2) by bol-van | `winws2.exe`, `lua/*.lua`, `files/fake/*.bin` | MIT, [text](licenses/zapret2.txt) | https://github.com/bol-van/zapret2 |
| [LuaJIT](https://luajit.org/) (OpenResty fork), statically linked into `winws2.exe` | | MIT, [text](licenses/LuaJIT.txt) | https://github.com/openresty/luajit2 |
| [zlib](https://zlib.net/) 1.3.1, statically linked into `winws2.exe` | | zlib, [text](licenses/zlib.txt) | https://github.com/madler/zlib |
| [WinDivert](https://reqrypt.org/windivert.html) 2.2 by basil00 | `WinDivert.dll`, `WinDivert64.sys` | LGPL-3.0 (dual-licensed LGPL-3.0 / GPL-2.0), [text](licenses/WinDivert.txt) | https://github.com/basil00/WinDivert |
| [Cygwin](https://cygwin.com/) DLL 3.4.10 | `cygwin1.dll` | LGPL-3.0-or-later, [LGPL](licenses/LGPL-3.0.txt), [GPL](licenses/GPL-3.0.txt) | https://cygwin.com/git.html |
| [sing-box](https://github.com/SagerNet/sing-box) 1.13.14 by nekohasekai (SagerNet) | `sing-box.exe` | GPL-3.0-or-later with the name clause, [notice](licenses/sing-box.txt), [GPL](licenses/GPL-3.0.txt) | https://github.com/SagerNet/sing-box/tree/v1.13.14 |
| [Wintun](https://www.wintun.net/) 0.14.1 by WireGuard LLC, embedded unmodified in `sing-box.exe`, which loads it to create the `Zarp` network adapter | `wintun.dll` (inside `sing-box.exe`) | Wintun Prebuilt Binaries License, [text](licenses/wintun.txt) | https://git.zx2c4.com/wintun |
| Country IP ranges of Russia, Iran and China from [ipverse/country-ip-blocks](https://github.com/ipverse/country-ip-blocks) | `Routes/*.txt` (data, not code) | CC0-1.0, [text](licenses/country-ip-blocks.txt) | https://github.com/ipverse/country-ip-blocks |

The complete corresponding source code of WinDivert, Cygwin, sing-box and Wintun is available from the upstream projects linked above. Wintun is not a separate file: it is carried inside `sing-box.exe` and used by sing-box through the public Wintun API. The versions of WinDivert and Cygwin follow the zapret2 release that is embedded at build time and may change when zapret2 is updated. The sing-box version is pinned in [`tools/fetch-singbox.ps1`](tools/fetch-singbox.ps1) together with the SHA-256 of the release archive, and `Zarp.exe` starts only the exact `sing-box.exe` it carries. sing-box is a separate program that Zarp starts and talks to through a configuration file and a local port; the MIT license of Zarp's own code is not affected, and the sing-box name is not used for anything else.

The same notices and license texts ship inside `Zarp.exe` and are written to `%LOCALAPPDATA%\Zarp\licenses` (Settings → Licenses).

Cloudflare and WARP are trademarks of Cloudflare, Inc. Zarp is an independent project, not affiliated with or endorsed by Cloudflare. Zarp does not include or redistribute any Cloudflare software. It only controls the WARP client through `warp-cli`; when the client is missing, Zarp can download the official installer directly from Cloudflare on the user's request and checks its Cloudflare signature before running it.
