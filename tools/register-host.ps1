# Registers the BDM native messaging host for Chromium browsers (per user, no admin).
param(
    [Parameter(Mandatory)] [string] $HostExe,
    [string] $ExtensionId = (Select-String -Path "$PSScriptRoot\..\extension\KEY_README.md" -Pattern 'Extension ID: `([a-p]{32})`').Matches[0].Groups[1].Value,
    [string[]] $Browsers = @('chrome', 'edge', 'brave')
)
& $HostExe --register $ExtensionId @Browsers
