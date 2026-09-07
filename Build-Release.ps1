param(
	[switch]$Beta
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$buildSuffix = if ($Beta) { "-beta" } else { "" }
$defineConstants = if ($Beta) { "AUTO_BETA" } else { "" }
$outputPath = Join-Path $projectRoot "Release"
$intermediatePath = Join-Path $projectRoot $(if ($Beta) { ".release-beta-obj" } else { ".release-obj" })
$buildOutputPath = Join-Path $projectRoot $(if ($Beta) { ".release-beta-bin" } else { ".release-bin" })
$projectPath = Join-Path $projectRoot "Auto.csproj"
$nativeSourcePath = Join-Path $projectRoot "Native\SystemUint"
$nativeCompilerPath = Join-Path $projectRoot ".tools\zig\zig-x86_64-windows-0.16.0\zig.exe"
$nativeOutputPath = Join-Path $nativeSourcePath "bin\SystemUint.Source.dll"
[xml]$projectFile = Get-Content -LiteralPath $projectPath -Raw
$appVersion = [string]$projectFile.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) {
	throw "Auto.csproj does not define Version."
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$defaultObjPath = Join-Path $projectRoot "obj"
$defaultBinPath = Join-Path $projectRoot "bin"
if (Test-Path -LiteralPath $defaultObjPath) {
	Remove-Item -LiteralPath $defaultObjPath -Recurse -Force
}
if (Test-Path -LiteralPath $defaultBinPath) {
	Remove-Item -LiteralPath $defaultBinPath -Recurse -Force
}

function Assert-FileUnlocked([string]$path, [int]$timeoutMilliseconds = 5000) {
	if (!(Test-Path -LiteralPath $path)) {
		return
	}
	$deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMilliseconds)
	do {
		try {
			$stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
			$stream.Dispose()
			return
		} catch {
			if ([DateTime]::UtcNow -ge $deadline) {
				throw "Release file is still locked after ${timeoutMilliseconds}ms: $path. Close Auto before building."
			}
			Start-Sleep -Milliseconds 200
		}
	} while ($true)
}

try {
	$releasePath = Join-Path $outputPath "Auto-$appVersion$buildSuffix.exe"
	$releaseSystemUintPath = Join-Path $outputPath "SystemUint.Source.dll"
	Assert-FileUnlocked $releasePath
	Assert-FileUnlocked $releaseSystemUintPath

	if (!(Test-Path -LiteralPath $nativeCompilerPath)) {
		throw "Native compiler not found: $nativeCompilerPath"
	}
	Push-Location $nativeSourcePath
	try {
		& $nativeCompilerPath c++ -target x86-windows-gnu -shared -O2 -Wno-nullability-completeness SystemUint.cpp SystemUint.def -o $nativeOutputPath -luser32 -lkernel32
		if ($LASTEXITCODE -ne 0) {
			throw "SystemUint.dll build failed with exit code $LASTEXITCODE."
		}
	} finally {
		Pop-Location
	}

	dotnet publish $projectPath `
		-c Release `
		-r win-x86 `
		--self-contained false `
		-p:PublishSingleFile=true `
		-p:PublishReadyToRun=false `
		-p:DebugType=None `
		-p:DebugSymbols=false `
		-p:AssemblyName="Auto-$appVersion$buildSuffix" `
		"-p:DefineConstants=$defineConstants" `
		-p:BaseIntermediateOutputPath="$intermediatePath\" `
		-p:BaseOutputPath="$buildOutputPath\" `
		-o $outputPath

	if ($LASTEXITCODE -ne 0) {
		throw "Release publish failed with exit code $LASTEXITCODE."
	}

	Write-Host "$(if ($Beta) { 'Beta' } else { 'Release' }) executable: $releasePath"
} finally {
	if (Test-Path -LiteralPath $intermediatePath) {
		Remove-Item -LiteralPath $intermediatePath -Recurse -Force
	}
	if (Test-Path -LiteralPath $buildOutputPath) {
		Remove-Item -LiteralPath $buildOutputPath -Recurse -Force
	}
}
