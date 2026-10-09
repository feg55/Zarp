# Regression tests

Run on Windows with the .NET SDK and .NET Framework 4.8:

```powershell
dotnet build tests/Zarp.Tests/Zarp.Tests.csproj -c Release -o build/tests
.\build\tests\Zarp.Tests.exe --results build/test-results
```

Each independent case runs in its own STA process with fresh test data. Cases run sequentially; an assertion failure, process crash or timeout does not prevent the remaining cases from running. A failed suite exits with code 1. A skipped case is reported as `SKIP`, with its reason. Real WARP connections and WinDivert interception are never started.

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

Reports are updated after every completed case. Default case timeout is 120 seconds; winws2 option parsing has a 30-second timeout. No failing case is automatically retried or turned into a pass.

The one exception is a winws2 launch that the Windows loader kills: exit code `0xC0000142` (`STATUS_DLL_INIT_FAILED`) with no output and no timeout. The program never read its arguments, and on GitHub runners this now and then happens to any strategy case whatever its configuration. Such a launch is repeated, up to 4 launches in total (`TestLoaderRetry` covers the rules). The case still fails if the last launch dies the same way. Every launch is kept in `winws2.txt`, and a case that passed after a repeat adds a `::warning` annotation to the run, so the repeats stay visible. Any other exit code, any output, a timeout or a start error is final.

## Running selected cases

```powershell
.\build\tests\Zarp.Tests.exe --list
.\build\tests\Zarp.Tests.exe --filter 'warp-t-multidisorder,restrict=False' --results build/strategy-results
.\build\tests\Zarp.Tests.exe --filter 'TestLogLayout[limit=749,scale=1.5,adjustment=7]' --results build/layout-results
```

The filter matches a case-insensitive substring. A filter matching no cases exits with an error.

`TestRunnerReporting` verifies the reporting mechanism with deliberate assertion failures, a process exit without a result, a timeout, a skip and a passing case after them. Its nested `expected failures with spaces` directory contains those intentional failures; the root `results.xml` contains the actual suite results.

`tools/fetch-zapret.ps1` supplies the embedded zapret2 used by native strategy cases. If the archive was absent at build time, those cases explicitly report `SKIP`. A present binary that cannot start or verify its configuration is a failure with process diagnostics.
