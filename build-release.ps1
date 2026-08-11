param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'CustomImageViewer.csproj'
$publishDirectory = Join-Path $projectRoot 'bin\publish\TagSeeker-win-x64'
$installerScript = Join-Path $projectRoot 'installer\CustomImageViewer.iss'
$releaseDirectory = Join-Path $projectRoot 'releases'
$projectXml = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
$portableZip = Join-Path $releaseDirectory "TagSeeker-$version-win-x64-portable.zip"

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

Write-Host 'TagSeeker Windows x64 자체 포함 배포 파일을 만드는 중입니다...'
dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishProfile=win-x64 `
    -p:PublishDir="$publishDirectory\"

$forbiddenFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
    $_.Name -eq 'settings.json' -or
    $_.Name -like 'tags.db*' -or
    $_.Extension -eq '.cache' -or
    $_.Extension -eq '.log'
}
if ($forbiddenFiles) {
    $names = $forbiddenFiles.FullName -join [Environment]::NewLine
    throw "사용자 데이터로 보이는 파일이 배포 폴더에 포함되어 있습니다:`n$names"
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
if (Test-Path -LiteralPath $portableZip) {
    Remove-Item -LiteralPath $portableZip -Force
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableZip -CompressionLevel Optimal
$portableHash = (Get-FileHash -LiteralPath $portableZip -Algorithm SHA256).Hash
Write-Host "Portable ZIP 생성 완료: $portableZip"
Write-Host "SHA-256: $portableHash"

if ($SkipInstaller) {
    Write-Host "배포 폴더 생성 완료: $publishDirectory"
    exit 0
}

$isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$commonIsccPath = Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'
$isccPath = if ($isccCommand) { $isccCommand.Source } elseif (Test-Path $commonIsccPath) { $commonIsccPath } else { $null }

if (-not $isccPath) {
    Write-Warning 'Inno Setup 6을 찾지 못해 설치 파일은 만들지 않았습니다.'
    Write-Host "배포 폴더는 정상적으로 생성되었습니다: $publishDirectory"
    Write-Host 'Inno Setup 6 설치 후 이 스크립트를 다시 실행하면 installer\output에 설치 파일이 생성됩니다.'
    exit 0
}

Write-Host 'Windows 설치 파일을 만드는 중입니다...'
& $isccPath $installerScript
Write-Host "완료: $(Join-Path $projectRoot 'installer\output')"
