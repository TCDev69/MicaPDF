# Script to generate Inno Setup installers for all platforms

$template = Get-Content "installer-template.iss" -Raw

# x64 Installer
$x64Script = $template -replace '^', '#define Win64' + "`r`n"
$x64Script = $x64Script -replace 'OutputBaseFilename=.*', 'OutputBaseFilename=MicaPDF-Setup-x64'
$x64Script = $x64Script -replace 'ArchitecturesAllowed=.*', 'ArchitecturesAllowed=x64'
$x64Script = $x64Script -replace 'ArchitecturesInstallIn64BitMode=.*', 'ArchitecturesInstallIn64BitMode=x64'
$x64Script += "`r`n[Files]`r`nSource: `"bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\publish\*`"; DestDir: `"{app}`"; Flags: ignoreversion recursesubdirs"
$x64Script | Out-File -FilePath "installer-x64.iss" -Encoding UTF8

# x86 Installer  
$x86Script = $template
$x86Script = $x86Script -replace 'OutputBaseFilename=.*', 'OutputBaseFilename=MicaPDF-Setup-x86'
$x86Script = $x86Script -replace 'ArchitecturesAllowed=.*', 'ArchitecturesAllowed='
$x86Script += "`r`n[Files]`r`nSource: `"bin\x86\Release\net8.0-windows10.0.22621.0\win-x86\publish\*`"; DestDir: `"{app}`"; Flags: ignoreversion recursesubdirs"
$x86Script | Out-File -FilePath "installer-x86.iss" -Encoding UTF8

# ARM64 Installer
$arm64Script = $template
$arm64Script = $arm64Script -replace 'OutputBaseFilename=.*', 'OutputBaseFilename=MicaPDF-Setup-ARM64'
$arm64Script = $arm64Script -replace 'ArchitecturesAllowed=.*', 'ArchitecturesAllowed=arm64'
$arm64Script = $arm64Script -replace 'ArchitecturesInstallIn64BitMode=.*', 'ArchitecturesInstallIn64BitMode=arm64'
$arm64Script += "`r`n[Files]`r`nSource: `"bin\ARM64\Release\net8.0-windows10.0.22621.0\win-arm64\publish\*`"; DestDir: `"{app}`"; Flags: ignoreversion recursesubdirs"
$arm64Script | Out-File -FilePath "installer-ARM64.iss" -Encoding UTF8

Write-Host "Generated installer-x64.iss, installer-x86.iss, installer-ARM64.iss"
