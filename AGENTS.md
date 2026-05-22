# Unity Agent Rules

This repository is a Unity project.

## Project Baseline

- Unity version: `2022.3.62f3`
- Render pipeline: URP `14.0.12`
- Notable packages and frameworks: QFramework, UniTask, Localization, TextMesh Pro, UGUI, Unity Test Framework
- Gameplay scripts are mostly under `Assets/Scripts`
- Existing architecture uses MonoBehaviours, QFramework `MonoSingleton`, `TypeEventSystem.Global`, UnityEvent inspector wiring, ScriptableObject config, and the custom `ServiceBase<T>` registry

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
- Preserve existing project patterns and naming conventions
- Avoid changing serialization-sensitive fields
- Avoid changing GUID-linked assets

## Performance

Compilation is expensive in Unity.

Static analysis is preferred over compilation unless the user explicitly requests runtime verification.
