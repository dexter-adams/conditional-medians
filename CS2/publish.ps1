param(
    # Publish: first upload (prints the new mod id; put it in ModId).
    # NewVersion: upload a new build of an existing mod.
    # Update: change the page (texts, images, tags) without a new build.
    # Each one changes the public Paradox Mods page, so the command is never implied.
    [Parameter(Mandatory)]
    [ValidateSet('Publish', 'NewVersion', 'Update')]
    [string]$Command,
    # The mod's name, typed at the prompt below; or given here, for a terminal
    # that cannot answer a prompt. It must match DisplayName exactly either way.
    [string]$ConfirmName
)
$ErrorActionPreference = 'Stop'
# The game's ModPublisher signs in with the Paradox account the game is signed in to.
$publisher = $env:CSII_MODPUBLISHERPATH
if (!$publisher -or !(Test-Path $publisher)) {
    throw 'ModPublisher not found. Install the modding toolchain from the game (Options > Modding), which sets CSII_MODPUBLISHERPATH.'
}
$config = Join-Path $PSScriptRoot 'Properties\PublishConfiguration.xml'
$settings = ([xml](Get-Content $config -Raw)).Publish
$modId = $settings.ModId.Value
if ($Command -ne 'Publish' -and !$modId) {
    throw "$Command needs the mod's id in ModId (PublishConfiguration.xml), which the first Publish prints."
}
if ($Command -eq 'Publish' -and $modId) {
    throw 'ModId is set, so this mod is already published: use -Command NewVersion or -Command Update.'
}
$aiTags = @($settings.SelectNodes('AiTag') | Where-Object { $_.GetAttribute('Value').Trim() })
if ($Command -eq 'Publish') {
    # Without a Disclosure ModPublisher uploads the mod with no AI disclosure at all.
    # On 2026-11-21 Paradox Mods offered no AI-disclosure tags for this game, so an
    # AiTag would stop the publish ("No AI disclosure tags are available"); it stays
    # optional in case tags are added later.
    $disclosure = $settings.SelectSingleNode('Disclosure')
    # ModPublisher reads the Value attribute when present, else the element's text.
    $disclosureText = if ($null -eq $disclosure) { '' } elseif ($disclosure.HasAttribute('Value')) { $disclosure.GetAttribute('Value') } else { $disclosure.InnerText }
    if (!$disclosureText.Trim()) {
        throw 'Decide the AI disclosure first: write it in Disclosure in PublishConfiguration.xml and uncomment it.'
    }
}
else {
    # ModPublisher requires a changelog for NewVersion and Update, and shows it on the page.
    if (!$settings.ChangeLog.Value -and !$settings.ChangeLog.InnerText.Trim()) {
        throw "Write this version's changes in ChangeLog (PublishConfiguration.xml) first."
    }
}

# Upload only the mod's own files, never a local diagnostics.txt.
$built = Join-Path $PSScriptRoot '..\dist\CS2'
$content = Join-Path $PSScriptRoot '..\dist\Publish\ConditionalMedians2'
$files = 'ConditionalMedians2.dll', 'ConditionalMedians2.mjs', 'ConditionalMedians2.css', 'ConditionalMedians2.mjs.LICENSE.txt'
foreach ($file in $files) {
    if (!(Test-Path (Join-Path $built $file))) { throw "Missing $file in dist\CS2: run build.ps1 first." }
}
$version = $settings.ModVersion.Value
$dllVersion = (Get-Item (Join-Path $built 'ConditionalMedians2.dll')).VersionInfo.ProductVersion
$uiVersion = (Get-Content (Join-Path $PSScriptRoot 'UI\mod.json') -Raw | ConvertFrom-Json).version
if ($Command -ne 'Update' -and ($dllVersion -ne $version -or $uiVersion -ne $version)) {
    throw "Versions differ: ModVersion $version, DLL $dllVersion (Source\AssemblyInfo.cs), UI module $uiVersion (UI\mod.json). Set all three, then run build.ps1."
}
if (Test-Path $content) { Remove-Item $content -Recurse -Force }
New-Item -ItemType Directory -Force $content | Out-Null
foreach ($file in $files) { Copy-Item (Join-Path $built $file) $content }

Write-Host "About to run ModPublisher $Command for '$($settings.DisplayName.Value)' version $version."
Write-Host "Files: $((Get-ChildItem $content | ForEach-Object Name) -join ', ')"
if ($Command -eq 'Publish') {
    Write-Host "AI disclosure: $disclosureText"
    if ($aiTags.Count) { Write-Host "AI disclosure tags: $(($aiTags | ForEach-Object { $_.GetAttribute('Value') }) -join ', ')" }
}
Write-Host 'This changes the public Paradox Mods page.'
if (!$ConfirmName) { $ConfirmName = Read-Host 'Type the mod name to continue' }
if ($ConfirmName -cne $settings.DisplayName.Value) { throw 'Cancelled: the name did not match, so nothing was uploaded.' }

# Image paths in the configuration are relative to this folder.
Push-Location $PSScriptRoot
try {
    & $publisher $Command $config -c (Resolve-Path $content).Path -v
    if ($LASTEXITCODE -ne 0) { throw "ModPublisher $Command failed." }
}
finally { Pop-Location }
