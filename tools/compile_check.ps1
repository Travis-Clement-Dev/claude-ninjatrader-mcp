# compile_check.ps1 — headless NinjaScript C# compile check against the LIVE NinjaTrader
# assemblies (Roslyn via Visual Studio MSBuild). This is our programmatic "compiler access":
# it catches C# errors BEFORE installing into NinjaTrader, using the same compiler + the same
# reference assemblies NinjaTrader uses (NinjaTrader.Vendor.dll carries the Indicator base and
# the Volumetric/market-data types; Core/Gui/SharpDX cover the rest).
#
# Convention: ship NinjaScript CLASS-ONLY (no "#region NinjaScript generated code" wrapper) —
# NinjaTrader's editor generates that region itself on compile; hand-authoring it caused a
# duplicate-wrapper error. This script strips a region if one is present, then compiles the class.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File compile_check.ps1 -Source "C:\path\Indicator.cs"
# Exit code 0 = clean; non-zero = compile errors (printed above).
param(
    [Parameter(Mandatory = $true)][string]$Source,
    # Adjust -MsBuild to your install (any VS edition/year, or the .NET SDK MSBuild).
    # Tip: $(& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -find MSBuild\**\Bin\MSBuild.exe)
    [string]$MsBuild  = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    [string]$NtBin    = "C:\Program Files\NinjaTrader 8\bin",
    [string]$NtCustom = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
)

if (-not (Test-Path $Source))  { Write-Output "ERROR: source not found: $Source"; exit 2 }
if (-not (Test-Path $MsBuild)) { Write-Output "ERROR: MSBuild not found: $MsBuild"; exit 2 }

$work = Join-Path $env:TEMP ("nsc_" + [IO.Path]::GetFileNameWithoutExtension($Source))
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Strip the generated-code region if present (compile the class only).
$raw = Get-Content -Raw $Source
$marker = "#region NinjaScript generated code"
$idx = $raw.IndexOf($marker)
$body = if ($idx -ge 0) { $raw.Substring(0, $idx).TrimEnd() + "`r`n" } else { $raw }
Set-Content -Path (Join-Path $work "src.cs") -Value $body -Encoding UTF8

$proj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework><UseWPF>true</UseWPF><PlatformTarget>x64</PlatformTarget>
    <LangVersion>13.0</LangVersion><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo><AssemblyName>NscCheck</AssemblyName>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath><NoWarn>1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="src.cs" />
    <Reference Include="System.Xml" />
    <Reference Include="System.ComponentModel.DataAnnotations" />
    <Reference Include="NinjaTrader.Vendor"><HintPath>$NtCustom\NinjaTrader.Vendor.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="NinjaTrader.Core"><HintPath>$NtBin\NinjaTrader.Core.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="NinjaTrader.Gui"><HintPath>$NtBin\NinjaTrader.Gui.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="SharpDX"><HintPath>$NtBin\SharpDX.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="SharpDX.Direct2D1"><HintPath>$NtBin\SharpDX.Direct2D1.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="SharpDX.DXGI"><HintPath>$NtBin\SharpDX.DXGI.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
"@
Set-Content -Path (Join-Path $work "check.csproj") -Value $proj -Encoding UTF8

& $MsBuild (Join-Path $work "check.csproj") /restore /t:Build /p:Configuration=Release /nologo /v:minimal /clp:NoSummary
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Output "`nCOMPILE OK: $Source" }
else             { Write-Output "`nCOMPILE FAILED (exit $code): $Source" }
exit $code
