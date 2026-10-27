<p align="center"><img src="docs/icon.png" width="88" alt="TailOrbit"></p>

<h1 align="center">TailOrbit</h1>

<p align="center">
  把文件拖到设备上，用 Taildrop 发过去。<br>
  Drag a file onto a device to send it with Taildrop.
</p>

<p align="center"><a href="#中文">中文</a> · <a href="#english">English</a></p>

<p align="center"><img src="docs/main.png" width="880" alt="TailOrbit 主界面 / main window"></p>

| 拖进来，对准 · Drag & aim | 发送中 · Sending | 送达 / 失败 · Delivered / Failed |
| :---: | :---: | :---: |
| <img src="docs/drag.png" alt="拖入 / drag"> | <img src="docs/send.png" alt="发送中 / sending"> | <img src="docs/done.png" alt="送达和失败 / delivered and failed"> |

<p align="center"><img src="docs/quick-panel.png" width="430" alt="快捷面板 / quick panel"><br><sub>Ctrl+Alt+A 快捷面板 · quick panel</sub></p>

## 中文

TailOrbit 是 [Tailscale](https://tailscale.com) 的一个 Windows 小工具。它把你的设备排成一张星图：这台电脑在中间，其他设备按连接方式排在三圈上。文件拖到哪台设备上，就用 Taildrop 发给它，进度和结果直接在图上看。

### 星图

- **内圈（实线）**：直连。
- **中圈（虚线）**：经 DERP 中继，或者还没探到走哪条路。
- **外圈（点线、空心）**：离线，收不了文件。

打开窗口时，TailOrbit 会对每台在线设备跑一次 `tailscale ping`，判断是直连还是中继，结果保留 5 分钟。探到直连的设备会沿着连线滑进内圈。

### 发文件

- **拖放**：把文件或文件夹拖进窗口，对准一台在线设备松手。对准离线设备时它会摇头，松手也不会发。
- **点选**：点一台设备，挑好文件就发。
- **文件夹**：先打包成 zip 再发，发完删掉临时包。
- **发送中**：设备外面出现进度环，光点从中间流向设备。鼠标移到设备上会出现 ×，点一下取消。
- **结果**：送达时设备变绿、打勾；失败时连线变红、设备摇头。

### 其他操作

- 点设备名：复制它的 Tailscale IP。
- 点中间这台电脑：打开「下载」文件夹，Tailscale 在 Windows 上把收到的文件放在这里。
- 左下角是最近 5 次发送：点一下在资源管理器里定位，拖到设备上再发一次，悬停能看到失败原因。
- 右上角齿轮：开机自启、退出。关掉窗口只是缩回托盘，左键点托盘图标再打开。

### 快捷面板 `Ctrl+Alt+A`

在资源管理器里选中文件，按 `Ctrl+Alt+A`，再按数字键 `1`–`9` 发给对应设备；也可以用 `↑` `↓` 选、`Enter` 发。没选文件时，会先弹出选文件的窗口。`Ctrl+C` 复制设备 IP，`Esc` 关闭。

### 系统要求

- Windows 10（1903 及以上）或 Windows 11，系统自带 .NET Framework 4.8，不用另装运行库。
- 已安装并登录 Tailscale，tailnet 允许 Taildrop。
- 能发给哪些设备由 Tailscale 决定：就是 `tailscale file cp --targets` 列出来的那些，一般是你自己名下的设备。

### 编译和运行

仓库里不放编译好的 exe。用 Windows 自带的 C# 编译器就能编，不需要装 Visual Studio：

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

- `TailOrbit.exe --tray`：只在托盘启动，不弹主界面。开机自启用的就是这个参数。
- 只会开一个。再运行一次 exe，会把已经开着的主界面拿到前面。
- `build.ps1` 会先关掉正在运行的 TailOrbit。

### 工作原理

TailOrbit 自己不联网，所有操作都交给 Tailscale 官方命令行：

| 做什么 | 调用 |
| --- | --- |
| 列出能收文件的设备 | `tailscale file cp --targets`，`tailscale status --json` |
| 判断直连还是中继 | `tailscale ping --c 3 --timeout 2s <IP>` |
| 发送 | `tailscale file cp --verbose <文件…> <IP>:` |
| 读取进度 | `tailscale debug watch-ipn` 里的 `OutgoingFiles` |

发送记录只追加写在本机 `%LOCALAPPDATA%\TailOrbit\history.jsonl`，出错日志 `error.log` 也在这个文件夹，不会上传到任何地方。

### 说明

- 界面目前只有中文。
- 这是个人项目，与 Tailscale Inc. 没有关联，也没有得到它的认可。Tailscale 和 Taildrop 是 Tailscale Inc. 的商标。
- 代码用 [MIT 协议](LICENSE)。图标照 Lucide / Feather 的画法绘制，它们的协议见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## English

TailOrbit is a small Windows companion for [Tailscale](https://tailscale.com). It lays your devices out as a star map: this PC sits in the middle and every other device sits on one of three rings, depending on how it is connected. Drop a file on a device and it goes out over Taildrop; progress and the result show up right on the map.

### The map

- **Inner ring (solid)**: direct connection.
- **Middle ring (dashed)**: through a DERP relay, or not probed yet.
- **Outer ring (dotted, hollow)**: offline; it can't receive files.

When the window opens, TailOrbit runs `tailscale ping` once against each online device to tell direct from relayed, and keeps the answer for 5 minutes. A device found to be direct slides along its line into the inner ring.

### Sending

- **Drag and drop**: drag files or folders into the window and release over an online device. An offline device shakes its head and nothing is sent.
- **Click**: click a device, pick files, and they are sent.
- **Folders** are zipped first; the temporary zip is deleted afterwards.
- **While sending**, a progress ring wraps the device and dots flow out from the center. Hover the device to get an ×; click it to cancel.
- **When done**, the device turns green with a check mark. On failure the line turns red and the device shakes.

### More

- Click a device name to copy its Tailscale IP.
- Click this PC in the middle to open your Downloads folder, where Tailscale on Windows saves received files.
- Bottom left are the last 5 transfers: click one to show it in File Explorer, drag it onto a device to send it again, hover to see why it failed.
- The gear at the top right holds "start with Windows" and "quit". Closing the window only hides it to the tray; left-click the tray icon to bring it back.

### Quick panel `Ctrl+Alt+A`

Select files in File Explorer, press `Ctrl+Alt+A`, then press `1`–`9` to send them to that device, or use `↑` `↓` and `Enter`. With nothing selected you get a file picker first. `Ctrl+C` copies the device IP and `Esc` closes the panel.

### Requirements

- Windows 10 (1903 or later) or Windows 11. Both ship with .NET Framework 4.8, so there is no runtime to install.
- Tailscale installed and signed in, with Taildrop allowed on your tailnet.
- Tailscale decides which devices can receive: the ones listed by `tailscale file cp --targets`, normally the devices you own.

### Build and run

No prebuilt exe is published. The C# compiler that comes with Windows is enough; no Visual Studio needed:

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

- `TailOrbit.exe --tray` starts in the tray without opening the main window. "Start with Windows" uses this flag.
- Only one copy runs. Launching the exe again brings the open main window to the front.
- `build.ps1` closes a running TailOrbit before it compiles.

### How it works

TailOrbit makes no network calls of its own. Everything goes through the official Tailscale CLI:

| What | Call |
| --- | --- |
| List devices that can receive files | `tailscale file cp --targets`, `tailscale status --json` |
| Tell direct from relayed | `tailscale ping --c 3 --timeout 2s <IP>` |
| Send | `tailscale file cp --verbose <files…> <IP>:` |
| Read progress | `OutgoingFiles` from `tailscale debug watch-ipn` |

The transfer history is appended to `%LOCALAPPDATA%\TailOrbit\history.jsonl` on your PC, next to `error.log`. Nothing is uploaded anywhere.

### Notes

- The interface is Chinese only for now.
- This is a personal project, not affiliated with or endorsed by Tailscale Inc. Tailscale and Taildrop are trademarks of Tailscale Inc.
- The code is under the [MIT License](LICENSE). The icons are drawn after Lucide / Feather; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for their licenses.
