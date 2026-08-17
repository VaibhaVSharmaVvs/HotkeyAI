<#
.SYNOPSIS
    Validate the adversarial corpus and print verdict-versus-expectation.

.DESCRIPTION
    Runs `hotkeyai validate` over every plan the generator wrote and compares the verdict
    with the manifest's WANT column, so a fix can be checked by re-running this rather than
    by re-reading the report. Also reproduces the reparse-point finding, which cannot be
    expressed as a static plan: it creates a directory junction inside the allowed root
    (no elevation needed) and asks the validator about a path that goes through it.

.EXAMPLE
    dotnet build src/HotkeyAI.Cli
    pwsh tools/security-review/run_adversarial_plans.ps1 C:\temp\adversarial
#>
param(
    [Parameter(Mandatory = $true)][string] $Directory,
    [string] $Cli = "src\HotkeyAI.Cli\bin\Debug\net10.0-windows\hotkeyai.exe"
)

# Deliberately not "Stop". A rejected plan makes the CLI exit non-zero and, under Windows
# PowerShell 5.1, writing its stderr through the pipeline surfaces as a NativeCommandError —
# which would abort this script on the first plan that is refused, i.e. on every plan that
# behaves correctly.
$ErrorActionPreference = "Continue"

if (-not (Test-Path $Cli)) { throw "Build the CLI first: dotnet build src/HotkeyAI.Cli" }
if (-not (Test-Path (Join-Path $Directory "manifest.tsv"))) {
    throw "Run gen_adversarial_plans.py against $Directory first."
}

$manifest = Import-Csv (Join-Path $Directory "manifest.tsv") -Delimiter "`t"

"{0,-34} {1,-9} {2}" -f "case", "verdict", "wanted"
"-" * 78

foreach ($row in $manifest) {
    $file = Join-Path $Directory ($row.case + ".json")
    if (-not (Test-Path $file)) { continue }

    & $Cli validate $file | Out-Null
    $verdict = if ($LASTEXITCODE -eq 0) { "ACCEPTED" } else { "rejected" }

    "{0,-34} {1,-9} {2}" -f $row.case, $verdict, $row.want
}

# ---- the reparse-point case, which no static plan can express ----
""
"reparse point through the allowed root:"

$junction = Join-Path $Directory "sysjunction"
if (Test-Path $junction) { [System.IO.Directory]::Delete($junction, $false) }
$null = New-Item -ItemType Junction -Path $junction -Target "C:\Windows\System32"

try {
    $target = Join-Path $junction "cmd.exe"
    $plan = [ordered]@{
        schemaVersion = 1
        name          = "junction escape"
        trigger       = [ordered]@{ type = "hotkey"; keys = @("CTRL", "ALT", "J") }
        actions       = @([ordered]@{ id = "a1"; type = "launch_process"; path = $target })
    }
    $planFile = Join-Path $Directory "junction-escape.json"
    [System.IO.File]::WriteAllText($planFile, ($plan | ConvertTo-Json -Depth 8))

    & $Cli validate $planFile | Out-Null
    $verdict = if ($LASTEXITCODE -eq 0) { "ACCEPTED" } else { "rejected" }

    "  policy verdict for a path inside the profile : $verdict"
    "  what that path really is                     : $((Get-Item $target).VersionInfo.FileDescription)"
    "  wanted                                       : rejected (resolve reparse points before comparing)"
}
finally {
    if (Test-Path $junction) { [System.IO.Directory]::Delete($junction, $false) }
}
