param([Parameter(Mandatory)][string]$GameDataDir)
$ErrorActionPreference = 'Stop'
$data = (Resolve-Path -LiteralPath $GameDataDir).Path
$repo = Split-Path (Split-Path $PSScriptRoot)
foreach ($file in 'GodotSharp.dll','0Harmony.dll','sts2.dll') {
    $null = [Reflection.Assembly]::LoadFrom("$data/$file")
}
$assembly = [Reflection.Assembly]::LoadFrom("$repo/bin/Release/net9.0/STS2_MCP.dll")
$mod = $assembly.GetType('STS2_MCP.McpMod', $true)
$type = $mod.GetNestedType('SequenceObservation', 'NonPublic')
$ctor = $type.GetConstructors()[0]
$fields = $ctor.GetParameters()
$hand = [Array]::CreateInstance($fields[2].ParameterType.GetElementType(), 0)
$warnings = [Activator]::CreateInstance($fields[7].ParameterType)
$range = [Activator]::CreateInstance($mod.GetNestedType('TargetHpRange','NonPublic'), @('TEST_0', 1, 50))
function Observation([string]$enemies, [string]$local) {
    $ctor.Invoke(@('id', 'en', $hand, 0, $local, $enemies, '[]', $warnings))
}
$original = Observation '[{"entity_id":"TEST_0","hp":50,"status":[]}]' '{"card_cost":1}'
$compare = $type.GetMethod('Compare', [Reflection.BindingFlags]'Instance,NonPublic')
function Assert-Policy([string]$enemies, [string]$local, [string]$expected) {
    $current = Observation $enemies $local
    $stop = $compare.Invoke($original, @($current, $range))
    if ([string]$stop.Reason -ne $expected) { throw "Expected '$expected', got '$($stop.Reason)' for $enemies and $local" }
}
Assert-Policy '[{"entity_id":"TEST_0","hp":49,"status":[]}]' '{"card_cost":1}' ''
Assert-Policy '[{"entity_id":"TEST_0","hp":0,"status":[]}]' '{"card_cost":1}' 'enemies_changed'
Assert-Policy '[]' '{"card_cost":1}' 'enemies_changed'
Assert-Policy '[{"entity_id":"TEST_0","hp":49,"status":[],"modded_effect":7}]' '{"card_cost":1}' 'enemies_changed'
Assert-Policy '[{"entity_id":"TEST_0","hp":49,"status":[]}]' '{"card_cost":2}' 'player_state_changed'
'Verified HP-only acceptance, dead and removed targets, modded fields and changed card cost.'

$recover = $mod.GetMethod('AddRecovery', [Reflection.BindingFlags]'Static,NonPublic')
$state = [Collections.Generic.Dictionary[string,object]]::new()
$handChoice = [Collections.Generic.Dictionary[string,object]]::new()
$handChoice['select_tool'] = 'mp_combat_select_card'
$handChoice['confirm_tool'] = 'mp_combat_confirm_selection'
$state['hand_select'] = $handChoice
$state['battle'] = [Collections.Generic.Dictionary[string,object]]::new()
$state['battle']['can_act'] = $false
foreach ($canConfirm in $false, $true) {
    $receipt = [Collections.Generic.Dictionary[string,object]]::new()
    $handChoice['can_confirm'] = $canConfirm
    $recover.Invoke($null, @($receipt, $state, $null))
    $expectedTool = if ($canConfirm) { $handChoice['confirm_tool'] } else { $handChoice['select_tool'] }
    if ($receipt['next_tool'] -ne $expectedTool) { throw "Expected $expectedTool during hand selection." }
}
'Verified hand selection and confirmation recovery.'
