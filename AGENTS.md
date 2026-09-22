# Repository Guidelines

## Project Structure & Module Organization

`Program.cs` is the composition root for the .NET 10 bot. Feature code is grouped by responsibility: `Bot/` and `Commands/` handle Mezon integration, `Music/` and `Playback/` manage queues and streaming, and `Media/` wraps yt-dlp, FFmpeg, and CDN preparation. Business services and entities live in `Application/` and `Domain/`; PostgreSQL, Redis, and cache implementations live in `Infrastructure/`. SFU publisher protocol code is under `Sfu/` and `Playback/`, while message-building code and visualization assets are in `Ui/` and `Assets/viz/`. Add SQL migrations sequentially under `Infrastructure/Persistence/Postgres/Migrations/`. Tests belong in `Mezube.Tests/`.

## Build, Test, and Development Commands

- `dotnet restore Mezube.sln` restores application and test dependencies.
- `dotnet build Mezube.sln -c Release` compiles the full solution with nullable analysis enabled.
- `dotnet test Mezube.Tests/Mezube.Tests.csproj -c Release` runs the xUnit suite.
- `dotnet format Mezube.sln --verify-no-changes` checks SDK-standard C# formatting before review.
- `$env:DOTNET_ENVIRONMENT='dev'; dotnet run --project Mezube.csproj` starts the bot with development settings.
- `docker compose up --build` starts the bot with PostgreSQL and Redis. Local execution also requires `yt-dlp` and `ffmpeg` on `PATH`.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, and braces for control flow. Keep nullable reference types clean and favor idiomatic async APIs for I/O; use the existing `ConfigureAwait(false)` pattern in library-style code. Name types, methods, and public members in `PascalCase`, locals and parameters in `camelCase`, private fields in `_camelCase`, and interfaces with an `I` prefix. Keep dependencies explicit through constructor injection and register them in `Program.cs`.

## Testing Guidelines

Use xUnit `[Fact]` tests in `*Tests.cs` files. Follow the current `Method_expected_behavior` naming pattern, isolate external services with fakes or stubs, and cover success, failure, cancellation, and queue/persistence edge cases. No numeric coverage threshold is configured; every behavior change should include focused regression tests.

## Commit & Pull Request Guidelines

History favors short imperative subjects such as `fix 403 yt-dlp, add retry policy`; use that style and avoid vague messages like `update`. Pull requests should explain user-visible behavior, note configuration or migration changes, link relevant issues, and report build/test results. Include screenshots when changing help text, embeds, buttons, or visualization output.

## Security & Configuration

Never commit tokens, credentials, runtime data, or generated media. Put secrets in ignored `appsettings.dev.local.json` / `appsettings.prod.local.json` files or environment variables such as `Mezon__Token`; keep shared defaults in tracked `appsettings*.json` files.
