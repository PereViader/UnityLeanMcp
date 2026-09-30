# Development Guidelines

- Support Windows, Linux, and macOS. Avoid assumptions about filesystem casing, path syntax, process discovery, or file sharing.
- Prefer simple, transactional operations with explicit ownership, recoverable state, and clear completion conditions.
- Treat Unity domain reloads as asynchronous and externally triggered. Persist recovery state before work that can be interrupted; do not rely on uninterrupted managed execution.
- Do not end an operation solely because time has elapsed. Wait until backing work completes, fails, or is stopped. Honor caller cancellation without claiming the backing work has finished. Use delays only for polling, retry pacing, or lifecycle settlement, never as operation deadlines.
- Support the current client and protocol only. Remove replaced commands, deprecated APIs, overloads, adapters, and their tests instead of adding compatibility fallbacks. This does not remove support for the package's declared Unity versions.
- Use [docs/README.md](docs/README.md) for architecture and topic navigation. Read only the linked sections relevant to the change.
- Update the owning documentation section when a change introduces or invalidates a lasting decision or non-obvious learning. Edit the relevant section instead of appending a change log. Keep each rule in one place; omit implementation inventories, unsupported guarantees, and routine refactor history.
- Run GitHub CLI (`gh`) commands outside the sandbox.
