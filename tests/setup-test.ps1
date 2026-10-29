# 安装程序测试（Windows，需要管理员权限，约 1 分钟）：
#   静默安装到带空格和中文的路径 → 检查文件、lib 目录、注册表、快捷方式、防火墙、data 写权限
#   → 启动安装好的程序，确认依赖能从 lib 加载 → 覆盖安装（升级）→ 静默卸载（保留数据）
#   → 再装一次，卸载并删除数据，确认目录清理干净。
# 用法：pwsh tests/setup-test.ps1 -Product Teacher -Setup publish\MdQuiz-Teacher-Setup-1.0.exe
param(
    [Parameter(Mandatory = $true)] [ValidateSet('Teacher', 'Student')] [string] $Product,
    [Parameter(Mandatory = $true)] [string] $Setup,
    [string] $Root = (Join-Path $env:SystemDrive 'MdQuiz 安装测试'),
    [string] $LogDir = (Join-Path ([IO.Path]::GetTempPath()) 'mdquiz-setup-test')
)
$ErrorActionPreference = 'Stop'
$script:failures = 0

$brand = '明点智慧考评系统'
$edition = @{ Teacher = '教师端'; Student = '学生端' }[$Product]
$displayName = "$brand $edition"
$exe = "MdQuiz.$Product.exe"
$installDir = Join-Path (Join-Path $Root $brand) $edition
$regKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MdQuiz.$Product"
$desktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) "$displayName.lnk"
$startLink = Join-Path (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) $brand) "$displayName.lnk"
$Setup = (Resolve-Path $Setup).Path
New-Item -ItemType Directory -Force $LogDir | Out-Null

function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "  [OK]   $what" }
    else { Write-Host "  [FAIL] $what"; $script:failures++ }
}

function Run-Setup([string] $file, [string[]] $arguments, [string] $logName) {
    $log = Join-Path $LogDir $logName
    # /log 放在前面：/D= 之后的内容都会当作路径
    $p = Start-Process -FilePath $file -ArgumentList (@("/log=`"$log`"") + $arguments) -PassThru -Wait
    Write-Host "  $([IO.Path]::GetFileName($file)) $($arguments -join ' ') -> 退出码 $($p.ExitCode)"
    if ($p.ExitCode -ne 0 -and (Test-Path $log)) { Get-Content $log | ForEach-Object { Write-Host "    $_" } }
    return $p.ExitCode
}

function Firewall-Rule-Exists {
    & netsh advfirewall firewall show rule name="MdQuiz Teacher" | Out-Null
    return $LASTEXITCODE -eq 0
}

# 快捷方式是否指向 target。.lnk 中的路径以 UTF-16 保存；WScript.Shell 按系统 ANSI 代码页读取，
# 在英文系统上会把中文变成 ?，所以直接在文件内容中查找（UTF-16 字符串可能从奇数偏移开始，两种对齐都试）
function Link-Points-To([string] $link, [string] $target) {
    if (-not (Test-Path $link)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($link)
    $even = [Text.Encoding]::Unicode.GetString($bytes)
    $odd = [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    return $even.Contains($target) -or $odd.Contains($target)
}

function Install([string] $logName) {
    # /D= 必须放在最后，路径中的空格不需要引号
    return Run-Setup $Setup @('/S', "/D=$installDir") $logName
}

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }

Write-Host "== 1. 静默安装到 $installDir"
Check ((Install "$Product-install.log") -eq 0) '安装成功（退出码 0）'
Check (Test-Path (Join-Path $installDir $exe)) "$exe"
Check (Test-Path (Join-Path $installDir "$exe.config")) "$exe.config"
Check (Test-Path (Join-Path $installDir 'lib\HandyControl.dll')) 'lib\HandyControl.dll'
Check (Test-Path (Join-Path $installDir 'lib\MdQuiz.Core.dll')) 'lib\MdQuiz.Core.dll'
Check (@(Get-ChildItem $installDir -Filter *.dll -File).Count -eq 0) '程序目录中没有散落的 DLL'
Check (Test-Path (Join-Path $installDir 'uninstall.exe')) 'uninstall.exe（卸载程序）'
Check (Test-Path (Join-Path $installDir 'uninstall.lst')) 'uninstall.lst（文件清单）'
if ($Product -eq 'Teacher') {
    Check (Test-Path (Join-Path $installDir 'lib\x64\SQLite.Interop.dll')) 'lib\x64\SQLite.Interop.dll'
    Check (Test-Path (Join-Path $installDir 'lib\x86\SQLite.Interop.dll')) 'lib\x86\SQLite.Interop.dll'
    Check (Test-Path (Join-Path $installDir 'samples\sample-questions.md')) 'samples\sample-questions.md'
    Check (Test-Path (Join-Path $installDir 'firewall-teacher.bat')) 'firewall-teacher.bat'
    Check (Firewall-Rule-Exists) '防火墙规则 MdQuiz Teacher'
}
$dataDir = Join-Path $installDir 'data'
$usersCanWrite = (Test-Path $dataDir) -and @((Get-Acl $dataDir).Access | Where-Object {
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-545' -and
        ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify
    }).Count -gt 0
Check $usersCanWrite 'data 目录允许 Users 修改'
$reg = Get-ItemProperty $regKey -ErrorAction SilentlyContinue
Check ($null -ne $reg -and $reg.DisplayName -eq $displayName) "控制面板卸载信息：$($reg.DisplayName) $($reg.DisplayVersion)"
Check ($null -ne $reg -and $reg.InstallLocation -eq $installDir) "InstallLocation = $($reg.InstallLocation)"
foreach ($link in @($desktopLink, $startLink)) {
    Check (Test-Path $link) "快捷方式 $link"
    Check (Link-Points-To $link (Join-Path $installDir $exe)) '  指向安装目录中的程序'
}

Write-Host '== 2. 启动安装好的程序（依赖从 lib 加载）'
$app = Start-Process -FilePath (Join-Path $installDir $exe) -PassThru
Start-Sleep -Seconds 12
$alive = -not $app.HasExited
$modules = @()
if ($alive) {
    try { $modules = @($app.Modules | ForEach-Object { $_.FileName }) } catch { }
    Stop-Process -Id $app.Id -Force
    $app.WaitForExit(10000) | Out-Null
}
Check $alive '程序启动后保持运行'
$fromLib = @($modules | Where-Object { $_ -like (Join-Path $installDir 'lib\*') })
$fromLib | ForEach-Object { Write-Host "    已加载 $_" }
if ($Product -eq 'Teacher') { Check (@($fromLib | Where-Object { $_ -like '*\lib\x*\SQLite.Interop.dll' }).Count -eq 1) '从 lib 加载 SQLite 原生库' }
$appLog = (Get-ChildItem (Join-Path $dataDir 'logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content -Raw) -join "`n"
Write-Host ($appLog | Out-String)
# 日志由 lib 中的 MdQuiz.Wpf.dll 写入；教师端写这一行之前已打开 SQLite 数据库
Check ($appLog -match ($edition + '启动')) "程序日志写入 data\logs（$edition 启动）"
Check ($appLog -notmatch '\[ERROR\]') '程序日志中没有错误'

Write-Host '== 3. 覆盖安装（升级）'
Check ((Install "$Product-upgrade.log") -eq 0) '覆盖安装成功'
Check (Test-Path (Join-Path $dataDir 'logs')) '数据保留'

Write-Host '== 4. 静默卸载（保留数据）'
Check ((Run-Setup (Join-Path $installDir 'uninstall.exe') @('/S') "$Product-uninstall.log") -eq 0) '卸载成功'
Start-Sleep -Seconds 6 # uninstall.exe 退出后再删除自己
Check (-not (Test-Path (Join-Path $installDir $exe))) "已删除 $exe"
Check (-not (Test-Path (Join-Path $installDir 'lib'))) '已删除 lib'
Check (-not (Test-Path (Join-Path $installDir 'uninstall.exe'))) '已删除 uninstall.exe'
Check (-not (Test-Path (Join-Path $installDir 'uninstall.lst'))) '已删除 uninstall.lst'
Check (Test-Path (Join-Path $dataDir 'logs')) '数据目录保留'
Check (-not (Test-Path $regKey)) '已删除控制面板卸载信息'
Check (-not (Test-Path $desktopLink)) '已删除桌面快捷方式'
Check (-not (Test-Path $startLink)) '已删除开始菜单快捷方式'
if ($Product -eq 'Teacher') { Check (-not (Firewall-Rule-Exists)) '已删除防火墙规则' }

Write-Host '== 5. 再次安装后卸载并删除数据'
Check ((Install "$Product-install2.log") -eq 0) '安装成功'
Check ((Run-Setup (Join-Path $installDir 'uninstall.exe') @('/S', '/deletedata') "$Product-uninstall2.log") -eq 0) '卸载成功'
Start-Sleep -Seconds 6
Check (-not (Test-Path $installDir)) '安装目录已删除'
Check (-not (Test-Path (Join-Path $Root $brand))) "上级目录 $brand 已删除"

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "安装程序测试（$displayName）：失败 $script:failures 项"
exit $(if ($script:failures -eq 0) { 0 } else { 1 })
