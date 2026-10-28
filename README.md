# 明点知识考核系统

Windows 局域网考试 / 答题软件（项目代号 MdQuiz），分为**教师端**和**学生端**两个程序。教师机作为服务器，学生机在同一局域网内自动找到教师机、登录答题，答案实时保存到教师机。

- 题型：单选、多选、判断、填空、简答（简答人工批改，其余自动判分）
- 题库：手动录入，或用 **Markdown** / **Excel** 批量导入；支持图片、公式、代码、表格
- 组卷：从题库手动挑题，或按题型 + 标签随机抽题；试卷可以按分组（文件夹）整理
- 考试：花名册 + 学号核对登录、倒计时自动收卷；考试中可以随时加减时间（±5 / ±10 分钟或自定义），学生端倒计时同步
- 监考：实时查看每名考生的登录、答题进度、断线和切屏；双击考生即可查看其逐题作答，并快速切换考生
- 登录后学号与电脑绑定：原电脑重启程序可以直接继续，换电脑必须由老师在监考页面允许
- 多选题计分规则每场考试单独设置：全对才得分 / 少选得一半 / 按比例给分
- 防作弊（每场可开关）：答题窗口全屏置顶，切换窗口会上报到监考页面
- 断线不丢答案：学生每改一题就上报，同时写入本机缓存；断线自动重连，教师端重启后可恢复考试
- 成绩：统计、分数段分布、按题批改与改分、查看考生答卷、导出 Excel（成绩表 + 答题明细）
- MCP 服务（可选）：Claude、Cursor、Cherry Studio 等 AI 客户端可以通过 MCP 协议查询题库和成绩、出题组卷、操作考试，权限分三档单独开关

## 运行环境

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 7 SP1 / 8.1 / 10 / 11（32 位或 64 位） |
| 运行库 | .NET Framework 4.8（Win10 1903 及以上、Win11 已自带；Win7 需安装） |
| 网络 | 教师机与学生机在同一局域网；教师机放行 TCP 47320、UDP 47321（MCP 允许局域网访问时还需放行 TCP 47330） |

> **Win7 注意**：先安装 SP1，再安装 [.NET Framework 4.8 离线安装包](https://dotnet.microsoft.com/download/dotnet-framework/net48)（约 110 MB）。部分精简版 / Ghost 版 Win7 还需要先装 KB4019990 补丁，WPF 程序才能运行。建议先在一台机房电脑上试装。

## 获取程序

**方式一：下载自动构建结果。** 每次推送代码后，GitHub Actions 会在 Windows 上编译并运行测试，在该次运行的 Artifacts 里可以下载：

- `MdQuiz-Teacher`：教师端（含 `samples` 示例文件和 `firewall-teacher.bat`）
- `MdQuiz-Student`：学生端

解压后即可运行，无需安装。

**方式二：自己编译**，见下文“开发与构建”。

## 使用流程

### 教师端（MdQuiz.Teacher.exe）

1. **首次运行**：设置管理密码（之后每次打开都要输入）。如果学生连不上教师机，以管理员身份运行一次 `firewall-teacher.bat`。
2. **学生**：新建班级并录入学生，或“导入花名册”（Excel / CSV，列为“学号、姓名、班级”，可先“下载模板”）。
3. **题库**：新建题目，或“导入 Markdown / 导入 Excel”。导入时可以顺便生成一份试卷。
4. **试卷**：左侧试卷列表按分组折叠显示。点左上角文件夹按钮“新建分组”；分组标题右键（或悬停时的“…”）可以在该分组新建试卷、重命名、删除分组（组内试卷移到“未分组”，不会删除）。编辑区的分组下拉框可以把试卷移到其他分组。新建试卷 →“从题库选题”或“随机抽题”→ 调整顺序和分值 → 保存。
5. **考试管理**：新建考试，选择试卷、参加的班级、时长、多选题计分规则、乱序 / 显示得分 / 防作弊开关 →“创建并开放登录”。列表中显示每场考试的开始时间和结束时间（考试中显示按当前时长计算的预计结束时间）。
6. **监考**：学生登录后显示在卡片中（登录、答题进度、断线、切屏次数）。人到齐后点“开始考试”；考试中倒计时下方可以 −10 / −5 / +5 / +10 分钟，或“自定义”任意加减（缩短后剩余时间不能少于 1 分钟），也可以提前“结束考试”（未交卷的自动收卷）。**双击学生卡片**打开“考生答卷”窗口，逐题查看作答、正确答案和预判得分（考试中每 3 秒自动刷新），用左侧列表、“上一位 / 下一位”或 PageUp / PageDown 快速切换考生。学生登录后只能在原来那台电脑上继续答题；电脑死机等需要换机时，在该学生的卡片上右键“允许换电脑重新登录”，学生再到新电脑上登录即可（已保存的答案会保留）。
7. **成绩与批改**：查看统计，点“批改 / 改分”按题给简答题打分（其他题型也可以改分），双击成绩表中的考生（或“查看答卷”）查看其答卷，最后“导出 Excel”。

导出 Excel、保存模板、备份数据库等产生文件的操作完成后，右下角的提示中可以直接“在资源管理器中打开”或“打开文件”。

### 学生端（MdQuiz.Student.exe）

1. 打开后自动在局域网中搜索教师机（只找到一台开放了考试的教师机时会自动连接）。搜不到时，手动输入教师机地址（教师端左下角有显示）。
2. 输入学号 → 核对姓名 → 确认登录 → 等待老师开始考试。
3. 答题：左侧答题卡可以跳题、标记；答案自动保存；时间到会自动交卷。
4. 交卷后显示结果（考试设置了“交卷后显示得分”时显示客观题得分）。

## Markdown 题库格式

完整示例见 [`samples/sample-questions.md`](samples/sample-questions.md)。

```markdown
## [单选 2分] 下列哪个数是**质数**？
- [ ] 4
- [x] 7
- [ ] 9
解析: 7 只能被 1 和它本身整除。
标签: 数学, 质数

## [多选 4分] 下列属于传输层协议的是？
A. TCP   B. UDP   C. IP   D. HTTP
答案: AB

## [判断 1分] 地球绕太阳公转一周约为一年。
答案: 对

## [填空 4分] 中国的首都是 ____，面积最大的省级行政区是 ____。
答案: 北京 | 北京市
答案: 新疆 | 新疆维吾尔自治区

## [简答 10分] 简述 TCP 三次握手的过程。
答案:
客户端发送 SYN，服务器回复 SYN+ACK，客户端再回复 ACK。
```

规则：

- 每道题以 `## [题型 分值] 题干` 开头。题型可写“单选 / 多选 / 判断 / 填空 / 简答”（也认“单选题”“问答”等写法），分值可以省略。
- 选择题选项用 `- [ ]` / `- [x]`（x 表示正确），或 `A. 选项` 再加一行 `答案: B`，也可以一行写多个选项。
- 填空题在题干里用 `___`（三个及以上下划线）表示空位，每个空写一行 `答案:`；同一个空有多个可接受答案时用 `|` 分隔。判分时忽略首尾空格、全角 / 半角和大小写。
- 简答题的 `答案:` 是参考答案，供老师批改时参考，可以写多行。
- 可选的 `解析:`、`标签:`。标签用于筛选和随机抽题。
- 题干支持 Markdown：加粗、行内代码、代码块、表格、列表、公式 `$x^2$` / `$$…$$`、图片 `![](images/a.png)`。图片路径相对于 md 文件，导入时会存进数据库。

Excel 导入的列为：`题型、题干、选项A…选项J、答案、解析、标签、分值`（多选答案写 `AB`；填空多个空用 `；` 分隔），教师端题库页可以“下载 Excel 模板”。

## MCP 服务（AI 助手接入）

教师端内置 [MCP（Model Context Protocol）](https://modelcontextprotocol.io/) 服务，AI 客户端连上后可以用自然语言让 AI 查询成绩、分析错题、批量出题组卷等。默认关闭，在“设置 → MCP 服务（AI 助手接入）”中开启：

1. 打开开关，按需修改端口（默认 47330），勾选开放的权限，点“保存并应用”。
2. 把设置页显示的连接地址（如 `http://127.0.0.1:47330/mcp`）填到 AI 客户端中。设置页提供了可直接复制的配置示例和 Claude Code 命令：

```json
{
  "mcpServers": {
    "mdquiz": { "type": "http", "url": "http://127.0.0.1:47330/mcp" }
  }
}
```

```bash
claude mcp add --transport http mdquiz http://127.0.0.1:47330/mcp
```

- 传输方式：Streamable HTTP（`POST /mcp`）；只支持旧版 HTTP+SSE 的客户端使用 `http://127.0.0.1:47330/sse`。Claude Desktop 等只支持本地命令的客户端可以通过 `npx mcp-remote http://127.0.0.1:47330/mcp` 转接。
- 访问范围：默认只监听 127.0.0.1，只有教师机本机的 AI 客户端能连接，不需要令牌。勾选“允许局域网访问”后监听所有网卡，并**强制**要求请求头 `Authorization: Bearer <令牌>`（令牌在设置页生成 / 复制 / 重新生成）。学生机也在同一局域网，请妥善保管令牌。浏览器发起的非本机来源（Origin）请求一律拒绝。
- 权限分三档，可分别开关（默认只开 ①），未开放的工具不会出现在工具列表中：

| 权限 | 工具 |
|---|---|
| ① 只读查询 | `get_overview` 概况、`search_questions` / `get_questions` / `list_tags` 题库、`list_papers` / `get_paper` 试卷、`list_classes` / `list_students` 学生、`list_exams` 考试、`get_monitor_status` 监考状态、`get_exam_results` 成绩、`get_student_answers` 考生答卷、`get_question_analysis` 逐题分析 |
| ② 编辑题库、组卷、创建考试 | `create_questions` / `update_question` / `delete_questions` 题目、`import_questions_markdown` 按 Markdown 格式导入（可同时生成试卷）、`create_paper` / `update_paper` / `random_paper` 试卷、`create_paper_group` / `move_papers_to_group` 分组、`create_exam` 创建考试 |
| ③ 考试控制与改分 | `open_exam` 开放登录、`start_exam` / `end_exam` / `close_exam`、`adjust_exam_time` 加减时间、`allow_student_relogin` 允许换电脑、`set_manual_score` 人工改分 |

- AI 做了编辑或控制操作时，教师端右下角会提示，并刷新当前页面；设置页可以查看最近的调用记录（日志也写入 `data/logs/`）。

## 项目结构

```
MdQuiz.sln
├─ src/
│  ├─ MdQuiz.Core/      netstandard2.0  共享核心：题目/试卷/考试模型、Markdown 题库解析与导出、
│  │                                      自动判分、学生试卷生成（乱序）、网络协议（TCP 长度前缀 + JSON）、
│  │                                      UDP 局域网发现、学生端会话 ExamClient（重连、本地缓存、交卷）
│  ├─ MdQuiz.Server/    netstandard2.0  教师端业务：SQLite 存储（Dapper + System.Data.SQLite，试卷分组等表结构自动升级）、
│  │                                      考试服务 ExamHost（登录、作答、交卷、计时收卷、加减时间）、成绩与批改、考生答卷、
│  │                                      Markdown / Excel 导入导出、随机组卷；
│  │                                      Mcp/：MCP 服务（自带的轻量 HTTP 服务、JSON-RPC、工具与权限分级）
│  ├─ MdQuiz.Wpf/       net48 WPF 库    界面共用：主题配色（基于 HandyControl）、标志（Assets/、Themes/Brand.xaml）、图标、转换器、
│  │                                      MarkdownView（Markdig + WpfMath 渲染题目）、右下角通知 ToastHost、答题选项 ChoiceButton
│  ├─ MdQuiz.Teacher/   net48 WPF 程序  教师端：登录、监考、考试管理、成绩批改、题库、试卷、学生、设置
│  └─ MdQuiz.Student/   net48 WPF 程序  学生端：连接教师机、登录、候考、答题（防作弊全屏）、交卷结果
├─ tests/MdQuiz.Tests/  net8.0 xUnit   解析、判分、数据库（含旧库升级）、Excel、完整考试流程（本机回环）、MCP 测试
├─ samples/             示例题库（Markdown）、示例花名册（CSV）
├─ scripts/             firewall-teacher.bat（教师机防火墙放行）
└─ .github/workflows/   Windows 自动构建、测试、打包
```

主要技术选型及原因：

- **.NET Framework 4.8 + WPF**：一个 exe 覆盖 Win7 SP1 ~ Win11。
- **TCP + JSON 自定义协议，不用 WebSocket**：.NET Framework 自带的 WebSocket 在 Win7 上不可用。
- **System.Data.SQLite 1.0.119**：原生库静态链接 C 运行库，Win7 不需要额外安装 VC++ 运行库。
- **不嵌浏览器内核**：WebView2 / CefSharp 对 Win7 的支持已停止，题目用 Markdig + WpfMath 原生渲染。
- **HandyControl**：支持 .NET Framework 4.x 和 Win7 的 WPF 控件库，中文友好。
- **MCP 用自带的 TcpListener HTTP 服务，不用 HttpListener**：HttpListener 监听局域网地址需要管理员权限登记 URL ACL；自带实现在 Win7 ~ Win11 上都无需额外配置。

## 网络与数据

- 学生端发 UDP 广播（端口 47321）查找教师机，教师机单播回复；随后通过 TCP（默认端口 47320，可在设置中修改）通信。因此**只有教师机需要放行防火墙**，学生机不需要。交换机隔离了广播时，学生手动输入教师机 IP 即可。
- MCP 服务（默认关闭）使用 TCP 47330（可修改），默认只监听本机。
- 数据目录：程序所在目录下的 `data` 文件夹（不可写时改用 `%LOCALAPPDATA%\MdQuiz\...`）。
  - 教师端：`data/mdquiz.db`（全部题库、试卷及分组、花名册、考试记录、设置）、`data/logs/`。旧版本的数据库在新版本首次启动时自动升级（已有试卷归入“未分组”）。
  - 学生端：`data/cache/`（作答本地缓存）、`data/settings.json`、`data/logs/`
- 机房装有**还原卡**时，请把教师端放在不还原的磁盘（如 D 盘），并定期在“设置”中备份数据库。
- 忘记管理密码：在命令行运行 `MdQuiz.Teacher.exe --reset-password`，确认后清除密码（不影响其他数据），下次启动重新设置。

## 开发与构建

需要 .NET SDK 8.0 或更高版本。Windows 上可以用 Visual Studio 2022 直接打开 `MdQuiz.sln`。

```bash
# 运行测试（Windows / Linux / macOS 均可）
dotnet test tests/MdQuiz.Tests/MdQuiz.Tests.csproj

# 编译并输出可运行的程序
dotnet publish src/MdQuiz.Teacher/MdQuiz.Teacher.csproj -c Release -o publish/MdQuiz-Teacher
dotnet publish src/MdQuiz.Student/MdQuiz.Student.csproj -c Release -o publish/MdQuiz-Student
```

- 在 Linux / macOS 上也能编译 WPF 项目（`EnableWindowsTargeting` 已开启），但程序只能在 Windows 上运行。
- 本机调试：同一台电脑可以同时运行教师端和学生端（学生端会通过 127.0.0.1 找到本机教师端）。

### 界面冒烟测试

两个程序都支持 `--smoke <目录>` 参数，用于自动化检查界面：使用独立的临时数据目录（不影响正式数据），导入示例题库和花名册，逐页、逐个对话框截图（含考生答卷窗口、MCP 设置），学生端通过界面点击作答各种题型（检查多选题能选中多个选项），并让教师端和学生端在本机完成一场真实考试；教师端还会启动 MCP 服务并用 HTTP 调用一次。同时记录绑定错误和未处理异常，有错误时退出码为 1。

```powershell
MdQuiz.Teacher.exe --smoke D:\smoke
MdQuiz.Student.exe --smoke D:\smoke --server 127.0.0.1:47320 --student 20240101
```

在 GitHub Actions 中手动运行 `build` 工作流并勾选“同时运行界面冒烟测试”，即可在 Windows 上执行，截图和日志会上传为 `smoke-results`。平时推送代码只做编译、单元测试和打包，不运行冒烟测试。

## 第三方组件

[HandyControl](https://github.com/HandyOrg/HandyControl)（MIT）、[Markdig](https://github.com/xoofx/markdig)（BSD-2）、[WpfMath](https://github.com/ForNeVeR/xaml-math)（MIT）、[Dapper](https://github.com/DapperLib/Dapper)（Apache-2.0）、[System.Data.SQLite](https://system.data.sqlite.org/)（Public Domain）、[MiniExcel](https://github.com/mini-software/MiniExcel)（Apache-2.0）、[Newtonsoft.Json](https://www.newtonsoft.com/json)（MIT）、[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)（MIT）、图标来自 [Material Design Icons](https://pictogrammers.com/library/mdi/)（Apache-2.0）。
