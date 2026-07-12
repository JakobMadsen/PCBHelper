[CmdletBinding()]
param(
    [string]$Version = "0.1.0-alpha",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$OutputDirectory = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $repo "artifacts\release" }
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output "PCBHelper-$Version-win-x64"

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$common = @("--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:DebugType=None", "-p:Version=$Version")
dotnet publish (Join-Path $repo "src\PCBHelper.Cli\PCBHelper.Cli.csproj") @common --output (Join-Path $stage "cli")
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }
dotnet publish (Join-Path $repo "src\PCBHelper.Mcp\PCBHelper.Mcp.csproj") @common --output (Join-Path $stage "mcp")
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed." }

Move-Item (Join-Path $stage "cli\PCBHelper.Cli.exe") (Join-Path $stage "pcbhelper.exe")
Move-Item (Join-Path $stage "mcp\PCBHelper.Mcp.exe") (Join-Path $stage "PCBHelper.Mcp.exe")
Remove-Item (Join-Path $stage "cli"), (Join-Path $stage "mcp") -Recurse -Force
Copy-Item (Join-Path $repo "LICENSE"), (Join-Path $repo "docs\agent-guide-v1.md") -Destination $stage
New-Item -ItemType Directory -Path (Join-Path $stage "fixtures") | Out-Null
$fixtureArchive = Join-Path $output "fixture.tar"
git -C $repo archive HEAD fixtures/kicad-getting-started-led -o $fixtureArchive
if ($LASTEXITCODE -ne 0) { throw "Could not archive the committed tutorial fixture." }
$fixtureTemp = Join-Path $output ("fixture-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $fixtureTemp | Out-Null
tar -xf $fixtureArchive -C $fixtureTemp
Copy-Item (Join-Path $fixtureTemp "fixtures\kicad-getting-started-led") -Destination (Join-Path $stage "fixtures\kicad-getting-started-led") -Recurse
Remove-Item $fixtureArchive, $fixtureTemp -Recurse -Force

$mcp = @{
    servers = @{
        pcbhelper = @{
            type = "stdio"
            command = (Join-Path $stage "PCBHelper.Mcp.exe")
            env = @{ PCBHELPER_MCP_PROFILE = "workflow"; PCBHELPER_ALLOWED_ROOTS = "C:\PCB" }
        }
    }
} | ConvertTo-Json -Depth 6
Set-Content -LiteralPath (Join-Path $stage "mcp.example.json") -Value $mcp -Encoding utf8

$zip = Join-Path $output "PCBHelper-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii
Write-Output $zip
