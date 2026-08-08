# Contributing to OrionRate

Thanks for taking the time to look at this. OrionRate is rate limiting the Orion family way: token-bucket and sliding-window algorithms whose refill/window math runs on an `OrionClock`, so limits are deterministically testable. The project is small and the bar for contributions is "does it make the package clearer, faster, or safer without expanding the public surface needlessly."

## Before you open a PR

For anything beyond a typo, a docs tweak, or a one-line fix, please open an issue first. Five minutes of alignment up front saves an afternoon of rework later. State:

- The use case you are trying to solve
- What you tried that did not work
- Whether you want to send the patch yourself or are flagging the gap

For typos, docs polish, comment fixes, single-line changes, please skip the issue and send a PR directly. Title it `docs: ...` or `chore: ...` so it is obvious from the queue.

## Local development

```bash
git clone https://github.com/tunahanaliozturk/OrionRate
cd OrionRate
dotnet restore
dotnet build -c Release
dotnet test
```

.NET 8 SDK is required. Multi-target builds may need 9.0 / 10.0 SDKs installed; the multi-target dimension is intentional and not optional.

Branch from `main`. Name the branch after intent: `feat/...`, `fix/...`, `docs/...`, `refactor/...`, `chore/...`, `test/...`.

## Pull request shape

- One conceptual change per PR. Refactors and behaviour changes go in separate PRs even if the diff feels small.
- Conventional Commits style commit subject (`feat:`, `fix:`, `docs:`, etc.).
- New behaviour comes with tests. Bug fixes come with a failing-before, passing-after test.
- Public API additions need XML doc comments. Breaking changes need a CHANGELOG entry.
- No `Co-Authored-By` trailers. The author of the PR is the author of the work.

## Coding style

- The repo enforces analyzer warnings as errors and `latest-recommended` analysis mode. Treat warnings as bugs.
- Match the surrounding code style. If the existing code does X, do X.
- Names are spelled out. No `mgr`, `svc`, `ctx`. The exceptions are well-known abbreviations (`Id`, `Db`, `Url`, `Ip`).
- Comments explain why, not what. The code already says what.
- All window / refill / retry-after math must run on the injected `OrionClock`, never on `DateTime.UtcNow` or `Stopwatch`. An algorithm that cannot be fast-forwarded under `FakeOrionClock` is a bug. The per-key check-and-consume must stay atomic — a change that lets concurrent requests over-admit is a correctness regression.

## Tests

- xUnit.
- Test names are sentences with underscores: `Draining_a_100_per_minute_bucket_throttles_request_101_and_refills_after_60s`.
- Tests must not sleep for real. Drive time with `FakeOrionClock`; a test that waits on the wall clock will be rejected.
- Coverage is a side effect of writing tests for behaviour, not a target in itself.

## Reporting bugs

Open an issue with:

- A minimal reproduction (one file, one method, ideally less than 50 lines)
- The actual behaviour vs the expected behaviour
- The runtime (`dotnet --info` output) and the package version

If the bug has security implications, please email the maintainer privately before opening a public issue.

## Security

Do not file public issues for vulnerabilities. Contact the maintainer directly. See [SECURITY.md](SECURITY.md) if present, otherwise email the address listed in the package NuGet metadata.

## Conduct

Be kind. We follow the [Code of Conduct](CODE_OF_CONDUCT.md). Disagreement is fine; rudeness is not.

## License

By submitting a pull request, you agree your contribution is licensed under the repo's [MIT License](LICENSE).
