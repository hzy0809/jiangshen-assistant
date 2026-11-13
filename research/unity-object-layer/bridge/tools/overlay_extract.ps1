$ErrorActionPreference = 'Stop'
$researchRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bridgeRoot = Split-Path $PSScriptRoot -Parent
$managedDir = 'C:/Users/yu/AppData/Local/JiangShen/pc/js_Data/Managed'
$ilspy = Join-Path $researchRoot 'tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll'
$firstpassTypes = @('PopupUI', 'TooltipUI', 'MeshScript', 'Database.BuildingArea')
$gameTypes = @('BuildingMonsterScript', 'BuildingBehaviourScript', 'SecretShopNPC', 'CheckLauncherVersion', 'FlowerMessUI', 'ChatBehaviour', 'AnnounceBehaviour', 'MessengerUI', 'MeditationUI', 'CapitalUI')
$records = foreach ($typeName in @($firstpassTypes) + @($gameTypes)) {
    $assemblyName = if ($typeName -in $firstpassTypes) { 'Assembly-CSharp-firstpass.dll' } else { 'Assembly-CSharp.dll' }
    $sourcePath = Join-Path $managedDir $assemblyName
    $artifactPath = Join-Path $bridgeRoot ('overlay-' + $typeName + '.cs.txt')
    & dotnet $ilspy --disable-updatecheck -t $typeName $sourcePath > $artifactPath
    if ($LASTEXITCODE -ne 0) { throw "Decompilation failed: $typeName" }
    [pscustomobject][ordered]@{ type = $typeName; assembly = $assemblyName; input_sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash; artifact = (Split-Path $artifactPath -Leaf); artifact_sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash }
}
[ordered]@{ method = 'offline ILSpy; target assemblies are not executed'; types = @($records) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bridgeRoot 'overlay-inputs.json') -Encoding utf8
$records | Select-Object type, assembly
