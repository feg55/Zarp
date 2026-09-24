# Third-party notices

Zarp's own code is licensed under the [MIT License](LICENSE).

`Zarp.exe` also embeds the third-party software listed below. On first run it is extracted to `%LOCALAPPDATA%\Zarp\zapret2` and started as a separate process (`winws2.exe`). Zarp does not link against any of it. All binaries are redistributed unmodified, exactly as published in the [zapret2 releases](https://github.com/bol-van/zapret2/releases).

| Component | Files | License | Source code |
|---|---|---|---|
| [zapret2](https://github.com/bol-van/zapret2) by bol-van | `winws2.exe`, `lua/*.lua`, `files/fake/*.bin` | MIT, [text](licenses/zapret2.txt) | https://github.com/bol-van/zapret2 |
| [LuaJIT](https://luajit.org/) (OpenResty fork), statically linked into `winws2.exe` | | MIT, [text](licenses/LuaJIT.txt) | https://github.com/openresty/luajit2 |
| [zlib](https://zlib.net/) 1.3.1, statically linked into `winws2.exe` | | zlib, [text](licenses/zlib.txt) | https://github.com/madler/zlib |
| [WinDivert](https://reqrypt.org/windivert.html) 2.2 by basil00 | `WinDivert.dll`, `WinDivert64.sys` | LGPL-3.0 (dual-licensed LGPL-3.0 / GPL-2.0), [text](licenses/WinDivert.txt) | https://github.com/basil00/WinDivert |
| [Cygwin](https://cygwin.com/) DLL 3.4.10 | `cygwin1.dll` | LGPL-3.0-or-later, [LGPL](licenses/LGPL-3.0.txt), [GPL](licenses/GPL-3.0.txt) | https://cygwin.com/git.html |

The complete corresponding source code of WinDivert and Cygwin is available from the upstream projects linked above. Versions follow the zapret2 release that is embedded at build time and may change when zapret2 is updated.

The same notices and license texts ship inside `Zarp.exe` and are written to `%LOCALAPPDATA%\Zarp\licenses` (Settings → Licenses).

Cloudflare and WARP are trademarks of Cloudflare, Inc. Zarp is an independent project, not affiliated with or endorsed by Cloudflare. It does not include any Cloudflare software and only controls a separately installed WARP client through `warp-cli`.
