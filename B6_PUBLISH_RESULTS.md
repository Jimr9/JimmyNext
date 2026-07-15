# Stage B6 — self-contained publish trial results

Command:

```
dotnet publish WSJTX_Controller\Jimmy.csproj -c Release -r win-x64 --self-contained true -o publish-trial
```

## Result

- **Measured size: 127,174,811 bytes (~122 MiB / 121.3 MB) across 240 files**, self-contained
  win-x64, Release configuration.
- Build succeeded with 2 pre-existing warnings (CS0168 unused exception variable in
  `WsjtxClient.Debug.cs` and `WsjtxClient.Protocol.cs`, both inside `#if DEBUG`-only usage —
  present before the migration, Release-config-only, unrelated to the .NET 10 port).

## Implication

Replaces the .NET Modernization Report's ~150MB community-reported estimate with a real,
measured number: **Jimmy's actual self-contained win-x64 publish size is smaller than
estimated, at ~122 MiB.**

The publish output itself (`publish-trial/`) is disposable build output and is not committed
to the repository -- re-run the command above to reproduce it.
