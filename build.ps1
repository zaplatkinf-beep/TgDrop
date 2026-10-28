param([string]$OutputPath = "$PSScriptRoot\TgDrop.exe")
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /out:"$OutputPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll "$PSScriptRoot\src\TgDrop.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
