# Run in PowerShell 7 with one Release bridge DLL and both installed game data directories.
param(
    [Parameter(Mandatory)][string]$BridgeDll,
    [Parameter(Mandatory)][string[]]$GameDataDir
)
$ErrorActionPreference = 'Stop'
$bridge = (Resolve-Path -LiteralPath $BridgeDll).Path
$verify = {
    param($bridge, $gameData)
    $ErrorActionPreference = 'Stop'
    foreach ($name in 'GodotSharp', '0Harmony', 'sts2') {
        $game = [Reflection.Assembly]::LoadFrom((Join-Path $gameData "$name.dll"))
    }
    $mod = [Reflection.Assembly]::LoadFrom($bridge).GetType('STS2_MCP.McpMod', $true)
    $previewCard = $mod.GetMethod('SafeBuildUpgradedCardPreview', [Reflection.BindingFlags]'Static,NonPublic')
    $strike = $game.GetType('MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad', $true)
    $db = $game.GetType('MegaCrit.Sts2.Core.Models.ModelDb', $true)
    $db::Inject($strike)
    $canonical = $db.GetMethod('Card').MakeGenericMethod($strike).Invoke($null, $null)
    foreach ($source in @($canonical, $canonical.ToMutable())) {
        $level = $source.CurrentUpgradeLevel
        $preview = $previewCard.Invoke($null, @($source))
        if ($null -eq $preview -or [object]::ReferenceEquals($source, $preview) -or
            $preview.CurrentUpgradeLevel -ne ($level + 1) -or $source.CurrentUpgradeLevel -ne $level) {
            throw "Expected an independently upgraded Strike preview using $gameData"
        }
    }
    "Verified canonical and mutable card previews using $gameData"
}

# A fresh process prevents the first loaded sts2.dll from masking a binding failure on the next version.
foreach ($directory in $GameDataDir) {
    $data = (Resolve-Path -LiteralPath $directory).Path
    & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -Command $verify -Args $bridge,$data
    if ($LASTEXITCODE -ne 0) { throw "Card preview verification failed using $data" }
}
