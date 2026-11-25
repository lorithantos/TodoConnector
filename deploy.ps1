<# Redeploys the code only. Usage: ./deploy.ps1 -AppName todo-mcp-lori [-ResourceGroup rg-todo-mcp] #>
param([Parameter(Mandatory)] [string] $AppName, [string] $ResourceGroup = 'rg-todo-mcp')
$ErrorActionPreference = 'Stop'
$publish = Join-Path ([IO.Path]::GetTempPath()) "todo-mcp-publish"; $zip = "$publish.zip"
Remove-Item $publish, $zip -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot/src/TodoMcp" -c Release -o $publish; if ($LASTEXITCODE) { throw 'publish failed' }
Compress-Archive -Path "$publish/*" -DestinationPath $zip
az webapp deploy -g $ResourceGroup -n $AppName --src-path $zip --type zip; if ($LASTEXITCODE) { throw 'deploy failed' }
