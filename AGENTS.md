# Agent rules

Canonical policy for every coding agent in this repo. Precedence: the user's current request, then
this file, then `docs/`.

Placeholders in `<angle brackets>` are filled in per project (see §0).

## 0. Project facts (fill in per project)

- Language(s) and pinned toolchain version: C# / .NET 10 SDK (`global.json`)
- Build command: `dotnet build Ryujinx.sln`
- Test command: `dotnet test`
- Format / lint command: `dotnet format` (validates against `.editorconfig`)
- Release build command: `dotnet build Ryujinx.sln -c Release`
- Fixture location and the env var that points to it: none — tests must be hermetic
  (no game dumps, no GPU). A test that needs a dump is skipped by default and says so.

## 1. Build and verify

- Build, test, and format with the project commands above. One build covers everything, including UI.
- Format after every code change; CI verifies formatting.
- Release artifacts come only from the release build.
- Exit status decides success. Never grep tool output: messages may be localized
  (`Fehler:` instead of `error:`), so a text match can report zero errors on a failed build.
- After a risky build, confirm the artifact actually changed (mtime or hash). A stale
  artifact reads exactly like a successful build.
- No "done", "fixed", or "works" without the command and its output.
- A new native or third-party dependency is stated in the change.

## 2. Language and boundaries

- New code uses the project's pinned language version (checked by CI). Read the installed
  library source for that exact version first; never write versioned APIs from memory.
- Emulators need native interop (SDL3, Vulkan, audio backends). New native code goes only
  through the existing interop layer; no new native dependency without a stated reason.
- One typed boundary between layers (HLE services, GPU, CPU, UI). No foreign types
  crossing it unless the project requires it.

## 3. Errors must fail loudly

Silence is worse than a crash: it produces a wrong result that looks valid.

- No empty catches, swallowed errors, silent defaults, silent retries, and no downgrading
  an error to a log line so the run can continue. A run either completes correctly or
  produces no output file. Stubs that fake success are bugs: they return `NotImplemented`
  or a named error, never a plausible zero or handle.
- No panic/abort/unchecked-unwrap/`unreachable` for anything a user or a guest program can
  trigger. Use a named error and a message.
- Every message names what failed and on which input: file path, guest address, service
  and command id, or byte offset. "Invalid input" alone is not a message.
- Refusals are data: kind, offending file, offending field or path, plus a user-facing
  sentence. The front end prints that sentence verbatim.
- Log at the point of failure, then return. Core records the message in the result and
  never writes to stdout; only the front end writes to its log and stderr.
- No partial output: caches write to a temp name and rename only after verification;
  temp files are deleted on error, cancel, and crash paths.
- Warnings are errors during development. If a validation cannot be performed, refuse;
  do not proceed with a guess.

## 4. Tests

- Every function with a branch, a loop, a parser, or arithmetic on untrusted guest data
  gets a test, including one test per named error (the failure side).
- Unit tests live beside the code in the idiomatic place (`src/Ryujinx.Tests`);
  integration tests use isolated temp dirs, never shared state. No test needs a game dump
  or a GPU; a test that does is skipped by default with a clear message.
- Tests never write into shared or fixture directories; output goes to temp dirs.
- Test names state the behavior. Assert concrete values (result codes, counts, addresses),
  not truthiness.
- A test that passes without its precondition present proves nothing. Say what it needed.

## 5. Parsing and untrusted input

Guest memory, IPC payloads, and file headers are untrusted input.

- Fixed-width integer types for on-disk and on-wire values. Never a platform-sized type
  (`nint`, `nuint`) for values read from the guest or a file.
- Bounds-check every read against the real input length first. Truncated input is the
  normal failure mode, not an edge case.
- Check arithmetic on guest- or file-controlled values for overflow before using the result
  for sizes, offsets, or allocations.
- Layouts are explicit (packed structs or byte-level decoding) with sizes asserted at
  compile time or in a test.
- Named constants for offsets, IDs, and flags, never inline literals.

## 6. Caches and disk

- Cap cache growth (shader cache, PPTC, translation caches) and abort before filling
  the disk. Temp usage stays bounded.
- Cache writes go to a temp name and rename only after verification.
- Never write next to user content unless the user chose the location.

## 7. Style

- Standard C# naming, explicit types where ReSharper settings require them, short functions,
  early returns, shallow nesting. No magic numbers, no unrelated churn.
- Comments explain why (spec fact, offset, hardware behavior, bug), never what the line does.
  Every unchecked or narrowing cast gets a why-comment naming the check that makes it safe.
- Keep module boundaries: HLE services do not know about the UI, GPU code does not parse
  IPC, writer code does not parse inputs.

## 8. Git and repository hygiene

- License identifier (SPDX header) on every source file, plus attribution where code is
  ported from another project (for example Eglantine behavior ports name the source).
- Small commits, one reason each. Build fixes and behavior fixes land separately.
- Never force-push, never rewrite history without a backup branch.
- A workaround is labelled `WORKAROUND`, states why, and logs every trigger.
  Silent workarounds are bugs.

## 9. When unsure

- If an API, offset, or hardware fact cannot be verified, stop and say so.
  Do not guess a field or a signature.
- Prefer refusing an input over producing output that may be wrong.
