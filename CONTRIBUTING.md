# Contributing

Argus is a Windows-native WinUI 3 application built on .NET 10.

## Development setup

1. Install the .NET 10 SDK and Visual Studio 2022 or newer with Windows app
   development tools.
2. Clone the repository.
3. Run:

```powershell
dotnet restore
dotnet build Argus.slnx
dotnet test Argus.slnx
dotnet run --project Argus.App\Argus.App.csproj
```

## Pull requests

- Keep changes focused and include tests for shared behavior.
- Do not commit databases, API keys, tokens, logs, local project paths, or
  screenshots containing personal data.
- Run `dotnet build Argus.slnx` and `dotnet test Argus.slnx` before opening a
  pull request.
- Describe user-visible changes and any migration or release impact.
