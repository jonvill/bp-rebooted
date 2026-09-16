<#
.SYNOPSIS
  Builds a release of Bad Piggies :: Rebooted and publishes it to the update feed.

.DESCRIPTION
  1. Builds the Windows player headless with Unity (BPREDevTools.BuildRelease).
  2. Only if the build succeeded: zips it, computes SHA-256 and signs the manifest with the
     release key (%USERPROFILE%\.bpre\update-signing-key.xml, created by the first run).
  3. Publishes BadPiggiesRebooted-<build>.zip and latest.json to -PublishDir.
     Every release build of the game checks <UpdateUrl>latest.json and updates itself.

  Unity must not have this project open while the script runs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\Release.ps1 -Notes "Fixed CTF pickup"
#>
param(
	[string]$ProjectPath = (Split-Path -Parent $PSScriptRoot),
	[string]$UnityExe = "C:\Program Files\Unity\Hub\Editor\2021.3.45f2\Editor\Unity.exe",
	[string]$BuildOutput = "E:\bp-rebooted-build",
	[string]$PublishDir = "E:\bp-share\release",
	[string]$UpdateUrl = "http://100.64.0.2:8088/release/",
	[string]$ConvenienceZip = "E:\bp-share\BadPiggiesRebooted-Multiplayer.zip",
	[string]$KeyFile = (Join-Path $env:USERPROFILE ".bpre\update-signing-key.xml"),
	[string]$Notes = "",
	[int]$KeepReleases = 3
)

$ErrorActionPreference = "Stop"
$ExeName = "BadPiggiesRebooted.exe"
if (-not $UpdateUrl.EndsWith("/")) { $UpdateUrl += "/" }

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# --- Preconditions ---------------------------------------------------------------------------
if (-not (Test-Path $UnityExe)) { throw "Unity not found: $UnityExe" }
if (-not (Test-Path $KeyFile)) { throw "Release signing key not found: $KeyFile" }
$projectFull = (Resolve-Path $ProjectPath).Path.TrimEnd('\')
$openEditors = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$projectFull*" -and $_.CommandLine -notlike "*-batchmode*" }
if ($openEditors) { throw "Close the Unity editor for $projectFull first (batch builds cannot open a project that is already open)." }

# --- Version ---------------------------------------------------------------------------------
$now = [DateTime]::UtcNow
# Minutes since 2020-01-01 UTC: monotonic and fits into a 32-bit int.
$build = [int][Math]::Floor(($now - [DateTime]::new(2020, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)).TotalMinutes)
$version = $now.ToString("yyyy.MM.dd-HHmm")
Step "Release $version (build $build)"

# --- Build -----------------------------------------------------------------------------------
$log = Join-Path ([IO.Path]::GetTempPath()) "bpre-release-$build.log"
Step "Building with Unity (log: $log)"
$unityArgs = @("-batchmode", "-quit", "-nographics",
	"-projectPath", "`"$projectFull`"",
	"-executeMethod", "BPREDevTools.BuildRelease",
	"-buildOutput", "`"$BuildOutput`"",
	"-buildNumber", "$build",
	"-buildVersion", "$version",
	"-updateUrl", "$UpdateUrl",
	"-logFile", "`"$log`"")
$proc = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru -Wait
$buildLine = Select-String -Path $log -Pattern "\[BPREDevTools\] Build:" | Select-Object -Last 1
if ($proc.ExitCode -ne 0 -or -not $buildLine -or $buildLine.Line -notmatch "Build: Succeeded") {
	Select-String -Path $log -Pattern "error CS|Build: |Aborting|Exception" | Select-Object -Last 10 | ForEach-Object { Write-Host $_.Line -ForegroundColor Yellow }
	throw "Build failed (exit code $($proc.ExitCode)). Nothing was published."
}
$infoFile = Join-Path $BuildOutput ("{0}_Data\StreamingAssets\bpre_build.json" -f [IO.Path]::GetFileNameWithoutExtension($ExeName))
if (-not (Test-Path $infoFile)) { throw "Build info missing: $infoFile. Nothing was published." }
Write-Host $buildLine.Line

# --- Package ---------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $PublishDir | Out-Null
$zipName = "BadPiggiesRebooted-$build.zip"
$zipPath = Join-Path $PublishDir $zipName
$zipTmp = "$zipPath.tmp"
Step "Packaging $zipName"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zipTmp) { Remove-Item $zipTmp -Force }
$sourceRoot = (Resolve-Path $BuildOutput).Path.TrimEnd('\')
$archive = [IO.Compression.ZipFile]::Open($zipTmp, [IO.Compression.ZipArchiveMode]::Create)
try {
	Get-ChildItem -Path $sourceRoot -Recurse -File |
		Where-Object { $_.FullName -notmatch "_BurstDebugInformation_DoNotShip" } |
		ForEach-Object {
			$relative = "BadPiggiesRebooted/" + $_.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
			[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
		}
}
finally { $archive.Dispose() }
Move-Item $zipTmp $zipPath -Force
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length

# --- Sign ------------------------------------------------------------------------------------
Step "Signing manifest"
$payload = "$build|$version|$zipName|$sha256|$size"
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString((Get-Content $KeyFile -Raw))
$sha = [System.Security.Cryptography.SHA256]::Create()
$signatureBytes = $rsa.SignData([Text.Encoding]::UTF8.GetBytes($payload), $sha)
$signature = [Convert]::ToBase64String($signatureBytes)

# The game verifies against the public key compiled into UpdateSigningKey.cs; make sure they match.
$keySource = Get-Content (Join-Path $projectFull "Assets\Scripts\Updater\UpdateSigningKey.cs") -Raw
$embedded = [regex]::Match($keySource, 'PublicKeyXml = "([^"]+)"').Groups[1].Value
$verifier = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$verifier.PersistKeyInCsp = $false
$verifier.FromXmlString($embedded)
if (-not $verifier.VerifyData([Text.Encoding]::UTF8.GetBytes($payload), [System.Security.Cryptography.SHA256]::Create(), $signatureBytes)) {
	throw "The signing key does not match the public key in UpdateSigningKey.cs. Nothing was published."
}

# --- Publish ---------------------------------------------------------------------------------
Step "Publishing to $PublishDir"
$manifest = [ordered]@{
	build = $build
	version = $version
	file = $zipName
	sha256 = $sha256
	size = $size
	notes = $Notes
	published = $now.ToString("o")
	signature = $signature
}
$manifestPath = Join-Path $PublishDir "latest.json"
$manifestTmp = "$manifestPath.tmp"
[IO.File]::WriteAllText($manifestTmp, ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
Move-Item $manifestTmp $manifestPath -Force

if ($ConvenienceZip) {
	Copy-Item $zipPath $ConvenienceZip -Force
}

Get-ChildItem -Path $PublishDir -Filter "BadPiggiesRebooted-*.zip" |
	Sort-Object Name -Descending |
	Select-Object -Skip $KeepReleases |
	Remove-Item -Force

Write-Host ""
Write-Host "Released $version (build $build)" -ForegroundColor Green
Write-Host ("  Package : {0} ({1:N1} MB)" -f $zipPath, ($size / 1MB))
Write-Host "  Feed    : ${UpdateUrl}latest.json"
