# HTML 转视频精简版

独立提取自 `html转视频工具/app/lib/convert.js`、`clock.js` 和 `page-scripts.js` 的核心流程，以 C# / .NET Framework 重新实现，不再携带 Node、Puppeteer、npm 依赖或内置浏览器。原项目未修改。

## 直接运行

双击 `dist/HtmlVideoLite.exe`。选择 HTML、输出位置，确认本机浏览器和 FFmpeg 的路径，然后转换。可直接拖入 HTML，或把 HTML 拖到 EXE 上。

当前本地目录已单独放置 `tools/ffmpeg.exe`，不依赖原项目文件夹，删除或移动原项目不影响新版。输出固定为兼容常见播放器的 H.264 / YUV420P MP4；有音轨时为 AAC。不需要管理员权限。

## 体积与依赖

| 项目 | 本地实测 |
| --- | --- |
| 旧 EXE | 154,109,440 字节，约 147 MB |
| 新 EXE | 约 75 KB，以构建输出为准 |
| 独立 FFmpeg | 64,458,752 字节，约 61.5 MB |
| 新版 EXE + 编码器 | 约 61.6 MB |

**约 75 KB 是程序本体，不是包含所有依赖的完整包。** 双击本体不需要 Node 或 Python，但转换需要已安装的 Edge/Chrome，以及 FFmpeg。此处未通过自解压或隐藏下载伪装成无依赖程序。

本地 FFmpeg 是从旧项目复用的 2018 年构建，只用于保留既有编码环境与本地验证，不代表推荐的新版本。也可以在界面选择自己的 FFmpeg；需支持 `libx264`、AAC、MJPEG 和 PNG 解码。程序不自动联网下载或安装依赖。

Windows 10/11、.NET Framework 4.8 和 Edge/Chrome 为目标环境；最终测试环境见 `VERIFICATION.md`。分发时保留如下结构，或自行选择现有 FFmpeg：

```text
HtmlVideoLite.exe
tools/
  ffmpeg.exe
```

FFmpeg 搜索顺序：`H2V_FFMPEG`、EXE 同目录、EXE 旁 `tools/`、EXE 上一级 `tools/`、PATH。浏览器也可通过 `H2V_BROWSER` 指定。

### 1.0.1 浏览器启动修复

旧版会把浏览器启动器的退出直接判断为启动失败。新版对独立 profile 的调试端口进行实际握手验证，允许启动器退出后由子进程接替；Edge 增加避免兼容层重复重启的启动参数。标准启动失败时，只针对同一浏览器再尝试一次软件渲染，不关闭沙箱、不修改系统设置。

启动失败会显示退出码和简短错误，完整诊断保存在 `%LOCALAPPDATA%\HtmlVideoLite\logs\last-browser-startup.log`。日志仅保存在本机，不上传。请关闭此前复制到桌面的 1.0.0 旧版，使用本目录重新构建的 1.0.1 程序；不会强制关闭或替换正在运行的桌面旧版。

## 功能

- 真正的离屏逐帧渲染，不录制桌面、不要求窗口前台、不产生逐帧图片文件。
- JS `requestAnimationFrame`、定时器、`Date.now`、`performance.now` 和 CSS / Web Animations 时间推进。
- 横屏、竖屏、自定义偶数宽高，1–120 fps、0.2–3600 秒、CRF 0–51。
- 自动读取页面时长变量、分镜数组、`data-duration`、时间码、媒体或有限 CSS 动画时长。无法可靠识别时明确要求手工填写，不猜测、不自动截断。
- 自动点击常见播放按钮；可填写 CSS 播放选择器。点击只执行一次，不会重复切换播放状态。
- 可指定舞台选择器、等比缩放舞台、保留完整视口、隐藏或保留舞台外控件。
- 外部音轨优先，否则读取 HTML 中的本地或 Base64 内嵌音轨。短音轨循环补齐、长音轨截到视频结束；可选择无音轨。
- 中文、空格、`#`、`%` 等路径字符；实时帧数、取消、错误日志、静态画面与资源缺失提示。
- 转换成功后才发布最终文件，取消或编码失败不会覆盖原输出。子进程放入私有 Windows Job，退出时只清理本程序创建的进程树。

## 使用边界

### 1.0.2 速度与截图超时

默认启用快速模式：JPEG 95 截图、速度优先的截图编码、FFmpeg `veryfast` 预设。JPEG 有轻微有损压缩，字体边缘或精细图形可能受影响；取消“快速模式”（命令行 `--precise`）恢复 PNG + `medium`。两种模式最终均编码为 H.264，PNG 模式不代表最终视频无损。更快预设可能增大输出文件。

截图使用独立调试连接，并激活浏览器内部录制标签页（不显示桌面窗口）。单帧截图超过 15 秒时，关闭失效连接，重建连接并对同一帧尝试显式区域截图；成功后继续使用备用方式，不跳帧、不重新开始时间轴。备用截图也超过 15 秒则明确报出帧号，不无限等待。此处理不保证修复浏览器渲染进程本身的卡死。两种截图均使用表面捕获；实测此环境下 `fromSurface=false` 会返回黑图，未采用该路径。

日志列出最多 20 条未加载的图片 / 媒体路径，本地音轨不存在会明确提示，不再误报成“不是本地文件”。缺失素材不会被自动替换。建议先修复素材，再用 1280×720、24 fps 试转；在相同片长下像素与帧数越少，需要的截图工作越少。

HTML 是任意程序，无法保证所有页面都能完全自动化。以下情况应先调整页面或配置：

- 网络图片、字体、脚本仍需网络；超时或损坏图片会报告，但不会偷偷删掉页面内容。
- 页面需要特定开始按钮时填写 CSS 选择器，例如 `#startBtn`。自动识别不调用任意全局函数，也不会对页面乱发多组快捷键。
- WebAudio 合成、麦克风、DRM、iframe 内独立时钟、Service Worker/Worker 时间轴、登录态或 `isTrusted` 检查不在通用逐帧保证范围内。
- 不录制系统声音。网络 / Blob 音轨不会自动下载，需另选音频文件。自动音轨是首个本地媒体资源，不会还原复杂多轨混音、分段延迟、音量包络；这类页面请先提供混好的音轨。
- 视频元素按虚拟播放起点寻帧；浏览器可解码的素材仍需正确加载。
- 不以“有定时器”冒充“画面在播放”；最终会检测是否整段完全静止，但不会因此拒绝静态 HTML 转视频。
- 单帧截图和 JS 脚本有超时；超大分辨率、复杂 3D 和极长页面可能需要大量时间、内存和磁盘空间。

## 命令行

EXE 同时提供命令行模式，退出码 0 为成功、1 为失败。Windows GUI EXE 在交互式终端不一定回显，因此自动化应使用 `--report` 并等待进程退出。

```powershell
$args = @('--input', 'D:\demo\page.html',
          '--output', 'D:\demo\video.mp4',
          '--duration', '12', '--width', '1920', '--height', '1080',
          '--fps', '30', '--crf', '18',
          '--report', 'D:\demo\result.json')
$p = Start-Process .\dist\HtmlVideoLite.exe -ArgumentList ($args | ForEach-Object { '"' + $_ + '"' }) -Wait -PassThru -WindowStyle Hidden
$p.ExitCode
```

其余参数：`--precise`（关闭快速模式）、`--audio 文件`、`--mute`、`--browser EXE`、`--ffmpeg EXE`、`--stage 选择器`、`--start 选择器`、`--no-fit`、`--keep-controls`、`--overwrite`。未指定 `--overwrite` 时拒绝覆盖现有文件；时长 0 为自动识别。GUI 会先询问是否覆盖。

固定帧率的时长取 `ceil(时长 × 帧率) / 帧率`，最多向上补不足一帧。

## 构建与测试

构建时使用 Python 标准库调用 Windows 自带 `csc.exe`，不需 pip、npm 或 SDK；Python 不参与最终程序运行。

```powershell
python build.py
python tests/smoke.py
python tests/harness.py
```

`build.py --version 1.0.23` 可覆盖构建版本，不修改源码或 VERSION。构建超过 300 KB 直接失败。验证数据和截图放 `build/smoke/`，不会编入 EXE。

GitHub Actions 已配置 main/master 推送、PR 和手动构建，版本号为 `MAJOR.MINOR.RUN_NUMBER`，发布前检查远端同名 tag。构建产物仅上传至 Releases，不上传 Actions Artifacts；PR 只编译、不发布。Release 仅包含 ASCII 命名 EXE，不上传 FFmpeg 或浏览器。仓库只跟踪源码、构建脚本、工作流、说明文档和测试夹具；EXE 与测试生成数据由本地构建或 CI 生成。提交和推送由用户手动操作，不由助手主动执行。

## 第三方编码器

`tools/ffmpeg.exe` 是本地复用的独立工具，不嵌入本项目源码或 EXE，默认不受 Git 跟踪。该构建启用 GPL / version3；如对外分发编码器，必须另外满足其源码提供、许可证及所含组件的分发义务。本项目的 CI 只发布自身 EXE，不重新分发该第三方二进制。
