# Regression tests

Run on Windows with the .NET SDK and .NET Framework 4.8:

```powershell
dotnet build tests/Zarp.Tests/Zarp.Tests.csproj -c Release -o build/tests
.\build\tests\Zarp.Tests.exe --results build/test-results
```

Each independent case runs in its own STA process with fresh test data. Cases run sequentially; an assertion failure, process crash or timeout does not prevent the remaining cases from running. A failed suite exits with code 1. A skipped case is reported as `SKIP`, with its reason. Real WARP connections, WinDivert interception and the Zarp network adapter are never started.

The cases for your own server (`TestProxy*`, `TestSingBox*`, `TestRouting*`, `TestGeoData*`) use the sing-box embedded by `tools/fetch-singbox.ps1`; without it they report `SKIP`. They run real sing-box processes, but only on localhost and without an adapter:

- `TestSingBoxConfigsAccepted` feeds every kind of generated configuration to `sing-box check`, which also rejects invalid RE2 expressions and CIDR ranges.
- `TestSingBoxLoopback[...]` starts a real VLESS, Trojan or Hysteria2 server (with the WebSocket, gRPC and HTTPUpgrade transports where they apply), connects the Zarp client to it and passes TCP and UDP traffic.
- `TestProxyMeasure` runs the connection check of Zarp through a real sing-box probe port: the SOCKS5 login and password, the refusals for a wrong or missing password, the `ip=` field that a genuine `cdn-cgi/trace` answer has, and the start of TLS inside the tunnel. The check page is replaced by a page on localhost.
- `TestProbeClient` feeds the same check, request by request, from a fake SOCKS5 server on localhost: answers with and without length, chunked answers, answers that never end, stubs without `ip=`, error codes, a wrong password, a reset during the handshake, a server that stays silent, cancelling and a server that answers TLS in plain text.
- `TestSingBoxTamper` changes the extracted `sing-box.exe` by one byte and checks that Zarp refuses to start it and that unpacking restores the genuine file.
- `TestRoutingEndToEnd` starts a Trojan server that redirects everything it receives to a marker, so the answer shows whether a connection went through the server or directly. It checks the priority of Block, Proxy and Direct, every kind of rule, GeoIP and GeoSite databases, and the per-app selection by process path (ignoring case).
- The engine flows for a server (`TestProxyMode*`) replace sing-box with a fake, so they need no process at all.
- `TestSingBoxLaunchRetry` checks when a launch that the Windows loader killed is repeated (see below). It starts no sing-box.

Built-in strategies have separate cases for each strategy and address restriction setting. Log layout cases are separate for every window height limit, scale and size adjustment. Assertions stop the affected case; later independent cases still run.

## Reading failures

The console prints `[RUN]`, `[PASS]`, `[FAIL]` or `[SKIP]` with the test name, parameters and duration, then a final list of failures. GitHub Actions also adds source annotations and a summary. The `zarp-test-results` artifact is uploaded after failures; test failures still fail the job and prevent publishing a release.

With the command above, reports are in `build/test-results`:

- `summary.md`: totals, failure details, skip reasons and every completed case.
- `results.xml`: JUnit report for the current run.
- `selected-tests.txt` and `current-test.txt`: selected cases and the active case if the job is interrupted.
- Dated run directories: environment, per-case results, stdout, stderr and process diagnostics.
- Strategy cases: `zarp.cfg` and `winws2.txt`, including the command, exit code in decimal and hex, timeout flag, stdout/stderr (explicitly marked when empty), embedded zapret2 version and binary SHA-256 hashes.
- UI image cases: PNG captures next to their case results.
- Windows in the cases are shown offscreen and never activated (`WS_EX_NOACTIVATE`, set in `PositionOffscreen`). An activated test window takes over the foreground, so keys typed in another window while the suite runs would vanish into it and show up as a stray character in a text box.
- Test data: every case works in its own `test-data\<id>` folder next to `Zarp.Tests.exe` (the path is printed after `Test data:` in the case output) with its own unpacked copies of zapret2 and sing-box, tens of megabytes each. A passing or skipped case deletes its folder, a failed case keeps it for inspection, and the next run removes the folders that are older than a day.

Reports are updated after every completed case. Writing a report is retried a few times if a virus scanner holds the file for a moment. Default case timeout is 120 seconds; winws2 option parsing has a 30-second timeout. No failing case is automatically retried or turned into a pass.

The one exception is a launch that the Windows loader kills: exit code `0xC0000142` (`STATUS_DLL_INIT_FAILED`) with no output and no timeout. The program never read its arguments, and on GitHub runners this now and then happens to a winws2 strategy case whatever its configuration. Such a launch is repeated, up to 4 launches in total (`TestLoaderRetry` covers the rules). The case still fails if the last launch dies the same way. Every launch is kept in `winws2.txt`, and a case that passed after a repeat adds a `::warning` annotation to the run, so the repeats stay visible. Any other exit code, any output, a timeout or a start error is final. The same rule covers the sing-box processes of the server cases, the `check` runs and the localhost servers (`TestSingBoxLaunchRetry` covers the rules). The Zarp client is started through `cmd`, which hides the exit code, so a client that ends without writing a line to its log counts as killed, and a real error always leaves a line. The repeat is written to the case output and raises the same `::warning`.

## Running selected cases

```powershell
.\build\tests\Zarp.Tests.exe --list
.\build\tests\Zarp.Tests.exe --filter 'warp-t-multidisorder,restrict=False' --results build/strategy-results
.\build\tests\Zarp.Tests.exe --filter 'TestLogLayout[limit=749,scale=1.5,adjustment=7]' --results build/layout-results
```

The filter matches a case-insensitive substring. A filter matching no cases exits with an error.

`TestRunnerReporting` verifies the reporting mechanism with deliberate assertion failures, a process exit without a result, a timeout, a skip and a passing case after them. Its nested `expected failures with spaces` directory contains those intentional failures; the root `results.xml` contains the actual suite results.

`tools/fetch-zapret.ps1` supplies the embedded zapret2 used by native strategy cases. If the archive was absent at build time, those cases explicitly report `SKIP`. A present binary that cannot start or verify its configuration is a failure with process diagnostics.
