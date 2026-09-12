$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore NovelGeneratePlugin.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw '锁定还原失败' }
    dotnet build NovelGeneratePlugin.slnx -c Debug --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    dotnet test NovelGeneratePlugin.slnx -c Debug --no-build
    if ($LASTEXITCODE -ne 0) { throw '测试失败' }
    dotnet format NovelGeneratePlugin.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw '格式检查失败' }
    & (Join-Path $PSScriptRoot 'check-docs.ps1')
    # XAML 不在 dotnet format 的全部覆盖面中，额外阻止本阶段改动留下尾随空白。
    git diff --check -- src tests tools docs README.md
    if ($LASTEXITCODE -ne 0) { throw '源码或文档存在 Git 空白错误' }
} finally { Pop-Location }
