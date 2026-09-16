$out = "artifacts/txtg-cli-win-x64"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish src/TxtgSharp.Cli/TxtgSharp.Cli.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o $out

Compress-Archive -Path "$out\*" `
  -DestinationPath "$out.zip" `
  -Force