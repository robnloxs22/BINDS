# Publishes Doink Binds for Windows as a single self-contained DoinkBinds.exe.
#
#   .\build\publish-windows.ps1              # x64
#   .\build\publish-windows.ps1 -Rid win-arm64
#   .\build\publish-windows.ps1 -Installer   # also builds an installer with Inno Setup (iscc.exe on PATH)
#
# Output: publish\<rid>\DoinkBinds.exe  (+ publish\DoinkBinds-Setup-<rid>.exe)
param(
    [string]$Rid = "win-x64",
    [switch]$Installer
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$project = Join-Path $root "src\DoinkBinds\DoinkBinds.csproj"

Write-Host "Publishing $Rid..." -ForegroundColor Cyan
dotnet publish $project -p:PublishProfile=$Rid
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Done: $root\publish\$Rid\DoinkBinds.exe" -ForegroundColor Green

if ($Installer) {
    $iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        $default = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        if (Test-Path $default) { $iscc = $default } else { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php" }
    }
    & $iscc "/DRid=$Rid" (Join-Path $root "build\windows\DoinkBinds.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
    Write-Host "Installer: $root\publish\DoinkBinds-Setup-$Rid.exe" -ForegroundColor Green
}
