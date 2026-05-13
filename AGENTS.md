# Unity Agent Rules

This repository is a Unity project.

## Never Do

- Never run `dotnet build`
- Never run `dotnet watch`
- Never run `dotnet test`
- Never invoke Unity batchmode compilation
- Never regenerate project files

## Preferred Validation

Use ONLY:

- VSCode diagnostics
- OmniSharp diagnostics
- Roslyn static analysis
- syntax inspection

## Code Editing

- Make minimal localized edits
- Avoid broad refactors
- Avoid changing serialization-sensitive fields
- Avoid changing GUID-linked assets

## Performance

Compilation is expensive in Unity.

Static analysis is preferred over compilation unless the user explicitly requests runtime verification.