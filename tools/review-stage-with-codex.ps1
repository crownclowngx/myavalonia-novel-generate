param(
    [Parameter(Mandatory = $true)][string]$CodexExecutable,
    [Parameter(Mandatory = $true)][ValidatePattern('^G[0-9]{4}$')][string]$Stage,
    [Parameter(Mandatory = $true)][string[]]$SourceFiles
)
$ErrorActionPreference = 'Stop'
$novelRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$novelOutput = Join-Path $novelRoot "artifacts/$Stage/codex-review"
New-Item -ItemType Directory -Path $novelOutput -Force | Out-Null
$novelParts = [Collections.Generic.List[string]]::new()
$novelParts.Add("请直接审查下方提供的 $Stage 小说插件 C# 源码，只分析此消息文本，不调用工具或请求确认。关注数据丢失、保存/关闭并发、事务、版本隔离和实际 SOLID 职责问题。最多输出 5 项可复现的缺陷，给出文件与触发流程。没有发现则说明范围限制。未实现的未来阶段不算缺陷。")
foreach ($novelSource in $SourceFiles) {
    $novelAbsolute = (Resolve-Path -LiteralPath (Join-Path $novelRoot $novelSource)).Path
    if (-not $novelAbsolute.StartsWith($novelRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($novelAbsolute) -ne '.cs') { throw '只允许提供当前项目中的明确 C# 文件' }
    $novelParts.Add("`n### $novelSource`n" + [IO.File]::ReadAllText($novelAbsolute))
}
$novelPromptPath = Join-Path $novelOutput 'prompt.txt'
[IO.File]::WriteAllText($novelPromptPath, ($novelParts -join "`n"))
Push-Location $novelRoot
try {
    # 源码仅经 stdin 作为审查材料；禁用插件和 hooks，使用既有登录，不接触认证缓存。
    Get-Content -Raw -LiteralPath $novelPromptPath | & $CodexExecutable exec --ignore-user-config --ephemeral --sandbox read-only `
        --disable hooks --disable plugins -m gpt-6-astra -c 'model_reasoning_effort="high"' --json `
        --output-last-message (Join-Path $novelOutput 'review.md') - `
        > (Join-Path $novelOutput 'events.jsonl') 2> (Join-Path $novelOutput 'stderr.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Codex 审查调用失败，请检查阶段 artifacts 中的结果；不得记为通过' }
    Get-Content -Raw -LiteralPath (Join-Path $novelOutput 'review.md')
} finally { Pop-Location }
