$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot 'check-docs.py')
if ($LASTEXITCODE -ne 0) { throw '文档链接检查失败' }
