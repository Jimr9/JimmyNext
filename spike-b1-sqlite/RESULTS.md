# Stage B1 spike results — SQLite / MQTTnet on net10.0-windows

Throwaway WinForms `net10.0-windows` project, `PackageReference` for
`System.Data.SQLite.Core` 1.0.118 and `MQTTnet`/`MQTTnet.Extensions.ManagedClient`
4.3.7.1207 (same versions as Jimmy's current `packages.config`).

## Result: PASS

- `dotnet build` succeeded, 0 warnings, 0 errors.
- Native interop DLL **is present** in the build output, but under a different
  layout than the `Stub.System.Data.SQLite.Core.NetFramework` package used today:
  RID-specific `runtimes\<rid>\native\SQLite.Interop.dll` (win-x64, win-x86, linux-x64,
  osx-x64) instead of a single flat `SQLite.Interop.dll` next to the exe.
- This RID-specific layout is resolved automatically by the .NET runtime at load
  time — no manual copy step, no `HintPath`/build-target plumbing needed (unlike
  the NetFramework stub's `.targets` import). Confirmed by running the built exe
  directly from `bin\Debug\net10.0-windows\`.
- Functional round-trip confirmed: created a table, inserted a row, read it back
  (`Round-trip read value: hello`), deleted the temp DB file.
- `MqttFactory().CreateMqttClient()` instantiated successfully.

## Implication for B3

`Jimmy.csproj`'s SDK-style conversion can drop the
`Stub.System.Data.SQLite.Core.NetFramework` package and its `.targets` import
entirely, replacing it with `System.Data.SQLite.Core` (the same package Jimmy
already effectively depends on transitively) as a plain `PackageReference`. No
special MSBuild plumbing is needed for the native interop DLL — it "just works"
via the runtimes folder convention.

This spike project is throwaway per the roadmap's own rollback plan and is not
part of `Jimmy.sln`.
