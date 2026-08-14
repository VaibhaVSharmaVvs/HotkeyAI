# Can the shipped reference automations actually register their hotkeys on a real machine?
#
# NOTE: an earlier version of this script named the lookup table $VK and the accumulator $vk.
# PowerShell variable names are case-insensitive, so they were the same variable: `$vk = 0`
# destroyed the table and every key resolved to 0, which made every combo look available.
# Distinct names below.

Add-Type -Namespace Spike -Name Hk2 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
[DllImport("user32.dll", SetLastError = true)]
public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
'@

$keyCodes = @{}
foreach ($ch in [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZ') { $keyCodes["$ch"] = [int][char]$ch }

function Test-Chord([string[]]$chord) {
    $modifierBits = 0
    $keyCode = 0
    foreach ($part in $chord) {
        switch ($part) {
            'CTRL'  { $modifierBits = $modifierBits -bor 2 }
            'ALT'   { $modifierBits = $modifierBits -bor 1 }
            'SHIFT' { $modifierBits = $modifierBits -bor 4 }
            'WIN'   { $modifierBits = $modifierBits -bor 8 }
            default { $keyCode = $keyCodes[$part] }
        }
    }
    if (-not $keyCode) { throw "unresolved key in chord: $($chord -join '+')" }

    $id = Get-Random -Minimum 30000 -Maximum 60000
    $ok = [Spike.Hk2]::RegisterHotKey([IntPtr]::Zero, [int]$id, [uint32]$modifierBits, [uint32]$keyCode)
    if ($ok) { [void][Spike.Hk2]::UnregisterHotKey([IntPtr]::Zero, [int]$id) }
    return [bool]$ok
}

$repo = "C:\Users\vaibhav\Desktop\Projects\HotkeyAI\examples"
$rows = foreach ($f in Get-ChildItem $repo -Filter *.json | Sort-Object Name) {
    $plan = Get-Content $f.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{
        Automation = $f.BaseName
        Chord      = ($plan.trigger.keys -join '+')
        Available  = Test-Chord $plan.trigger.keys
    }
}
$rows | Format-Table -AutoSize

Write-Output '--- how crowded are the usual namespaces? ---'
foreach ($prefix in @(@('CTRL','ALT'), @('CTRL','SHIFT'), @('CTRL','ALT','SHIFT'))) {
    $taken = @()
    foreach ($ch in [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZ') {
        if (-not (Test-Chord ($prefix + "$ch"))) { $taken += "$ch" }
    }
    $label = $prefix -join '+'
    if ($taken.Count -eq 0) { Write-Output "$label + <letter>: all 26 free" }
    else { Write-Output "$label + <letter>: $($taken.Count) taken -> $($taken -join ' ')" }
}
