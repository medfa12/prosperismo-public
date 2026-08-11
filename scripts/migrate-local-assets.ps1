param(
    [string]$ProsperismoRoot = 'C:\prosperismo',
    [string]$SharpEmuRoot = 'C:\sharpemu'
)

$ErrorActionPreference = 'Stop'

function Assert-UnderRoot([string]$Path, [string]$Root) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside $Root`: $fullPath"
    }
}

function Move-DirectoryWithCompatibilityJunction(
    [string]$Source,
    [string]$Destination
) {
    if (Test-Path -LiteralPath $Source -PathType Container) {
        $sourceItem = Get-Item -LiteralPath $Source -Force
        if ($sourceItem.LinkType -eq 'Junction' -and
            (Test-Path -LiteralPath $Destination -PathType Container)) {
            Write-Host "already migrated: $Source -> $Destination"
            return
        }
    }

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        if (Test-Path -LiteralPath $Destination -PathType Container) {
            New-Item `
                -ItemType Junction `
                -Path $Source `
                -Target ([System.Management.Automation.WildcardPattern]::Escape($Destination)) |
                Out-Null
            Write-Host "restored compatibility junction: $Source -> $Destination"
            return
        }
        Write-Host "skip missing directory: $Source"
        return
    }

    Assert-UnderRoot $Source $SharpEmuRoot
    Assert-UnderRoot $Destination $ProsperismoRoot
    if (Test-Path -LiteralPath $Destination) {
        throw "Destination already exists: $Destination"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.Directory]::Move($Source, $Destination)
    New-Item `
        -ItemType Junction `
        -Path $Source `
        -Target ([System.Management.Automation.WildcardPattern]::Escape($Destination)) |
        Out-Null
    Write-Host "moved directory: $Source -> $Destination"
}

function Move-FileWithCompatibilityHardLink(
    [string]$Source,
    [string]$Destination
) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        Write-Host "skip missing file: $Source"
        return
    }

    Assert-UnderRoot $Source $SharpEmuRoot
    Assert-UnderRoot $Destination $ProsperismoRoot
    if (Test-Path -LiteralPath $Destination) {
        throw "Destination already exists: $Destination"
    }

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Move-Item -LiteralPath $Source -Destination $Destination
    New-Item -ItemType HardLink -Path $Source -Target $Destination | Out-Null
    Write-Host "moved file: $Source -> $Destination"
}

$sourceGames = Join-Path $SharpEmuRoot 'games'
$targetGames = Join-Path $ProsperismoRoot 'games'
$oracle = Join-Path $ProsperismoRoot 'ps5oracle'

$gameDirectories = @(
    'asTRO.BOT-PPSA21564-USA-Game-v01.007.000-PS5',
    'gta v PPSA04264 [ 01.005.000 ]',
    'minecraft-PPSA17221-app',
    'superliminal'
)
foreach ($name in $gameDirectories) {
    Move-DirectoryWithCompatibilityJunction `
        (Join-Path $sourceGames $name) `
        (Join-Path $targetGames $name)
}

$runtimeDirectories = @('logs', 'savedata', 'session_ledger', 'shader_cache')
foreach ($name in $runtimeDirectories) {
    Move-DirectoryWithCompatibilityJunction `
        (Join-Path $sourceGames $name) `
        (Join-Path $targetGames $name)
}
Move-FileWithCompatibilityHardLink `
    (Join-Path $sourceGames 'config.toml') `
    (Join-Path $targetGames 'config.toml')

$sonyDirectories = @(
    '12.40 system dump',
    '3.02',
    '300REC',
    'firmware_kernels',
    'prospero-firmware-symbols',
    'prospero-internal',
    'prospero-sdk-10.00',
    'prospero-sdk-10.00-tools',
    'prospero-sdk-4.00-headers',
    'PS5_4.03_reconstructed',
    'PS5_9.00_decrypted',
    'rnps_4.02',
    'useful rnps'
)
foreach ($name in $sonyDirectories) {
    Move-DirectoryWithCompatibilityJunction `
        (Join-Path $sourceGames $name) `
        (Join-Path (Join-Path $oracle 'sony') $name)
}

$publicReferenceDirectories = @(
    'amd-isa-public',
    'llvm-amdgpu',
    'mesa-aco',
    'psdevwiki_ps5'
)
foreach ($name in $publicReferenceDirectories) {
    Move-DirectoryWithCompatibilityJunction `
        (Join-Path $sourceGames $name) `
        (Join-Path (Join-Path $oracle 'public-references') $name)
}
Move-FileWithCompatibilityHardLink `
    (Join-Path $sourceGames 'PUBLIC-ISA-REFERENCES-README.md') `
    (Join-Path (Join-Path $oracle 'public-references') 'PUBLIC-ISA-REFERENCES-README.md')

$researchDirectories = @('coredumps', 'do notopen', 'gpu shit_forzen')
foreach ($name in $researchDirectories) {
    Move-DirectoryWithCompatibilityJunction `
        (Join-Path $sourceGames $name) `
        (Join-Path (Join-Path $oracle 'research') $name)
}

$sourceInspiration = Join-Path $SharpEmuRoot 'inspiration'
$referenceTarget = Join-Path $oracle 'reference-projects'
Get-ChildItem -LiteralPath $sourceInspiration -Directory | ForEach-Object {
    Move-DirectoryWithCompatibilityJunction `
        $_.FullName `
        (Join-Path $referenceTarget $_.Name)
}

$evidenceMoves = @(
    @('C:\sharpemu-ps5-shell-evidence', (Join-Path $oracle 'evidence\shell-rendering')),
    @('C:\sharpemu-ps5-shell-execution-evidence', (Join-Path $oracle 'evidence\shell-execution'))
)
foreach ($move in $evidenceMoves) {
    $source = $move[0]
    $destination = $move[1]
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        Write-Host "skip missing evidence directory: $source"
        continue
    }
    Assert-UnderRoot $destination $ProsperismoRoot
    if (Test-Path -LiteralPath $destination) {
        throw "Destination already exists: $destination"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
    New-Item -ItemType Junction -Path $source -Target $destination | Out-Null
    Write-Host "moved evidence: $source -> $destination"
}

Write-Host 'local asset migration complete'
