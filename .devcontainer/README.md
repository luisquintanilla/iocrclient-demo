# Dev container

Use this when you want the same slide and sample environment everywhere.

It gives you:

- Node for RevealJS build and preview
- .NET 10 SDK for the file-based C# samples
- Azure CLI, in case a talk grounds against keyless Azure OpenAI

## Open it

- Codespaces: open the repo in a codespace.
- Local: install Docker and the VS Code Dev Containers extension, then reopen in container.

## Run

```bash
npm install
npm run preview
```

Open http://localhost:8000. Press `S` for speaker view.

Run a sample to check the grounding loop:

```bash
dotnet run samples/01-hello-dotnet.cs
```

See `samples/README.md` for the local-model and cloud-model samples. For a real AI claim, run the
smallest provider call, set the needed token, and paste the real output into `slides.md`.
