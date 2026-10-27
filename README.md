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

窗口顶上有五页：**星网**（发文件、收文件、连设备）、**分享**、**网络**、**云盘**、**本机**，`Ctrl+1`–`5` 切换。

### 星图

- **内圈（实线）**：直连。
- **中圈（虚线）**：经 DERP 中继，或者还没探到走哪条路。
- **外圈（点线、空心）**：离线，收不了文件。

打开窗口时，TailOrbit 会对每台在线设备跑一次 `tailscale ping`，判断是直连还是中继，结果保留 5 分钟。探到直连的设备会沿着连线滑进内圈。

### 发文件

- **拖放**：把文件或文件夹拖进窗口，对准一台在线设备松手。对准离线设备时它会摇头，松手也不会发。
- **点选**：点一台设备，在弹出的菜单里点「发文件」，挑好文件就发。
- **文件夹**：先打包成 zip 再发，发完删掉临时包。
- **发送中**：设备外面出现进度环，光点从中间流向设备。鼠标移到设备上会出现 ×，点一下取消。
- **结果**：送达时设备变绿、打勾；失败时连线变红、设备摇头。

### 收文件

别的设备发文件过来时，光点从那台设备流进中间，这台电脑外圈走进度、圆盘里显示百分比；收完打勾，文件出现在左下角（右上角是收件标，没点开看过的整块亮着）。

### 设备菜单

点一台设备，四周弹出一圈按钮，旁边的牌子写着指着的那个会做什么：

- **终端**：用 `~/.ssh/config` 里指向这台设备的别名打开 `ssh`（有 Windows Terminal 就在里面开）。没有别名时，Linux 设备和开了 Tailscale SSH 的设备直接用域名。
- **读剪贴板**：只有 Mac 有，把它剪贴板里的字读到这台电脑上（见下面「剪贴板互通」）。
- **网页**：设备上开着常见的网页端口时才有，在浏览器里打开。
- **发文件**、**复制域名**。
- **掉线提醒**：打开后这台设备离线、恢复时都会弹 Windows 通知（每 30 秒看一次），设备右上角挂个铃铛。

### 其他操作

- 点设备名：复制它的 Tailscale IP。
- 点中间这台电脑：打开「下载」文件夹，Tailscale 在 Windows 上把收到的文件放在这里。
- 左下角是最近 5 次发送和收到的文件：点一下在资源管理器里定位，拖到设备上再发一次，悬停能看到失败原因。
- 关掉窗口只是缩回托盘，左键点托盘图标再打开，右键托盘图标可以退出。

### 快捷面板 `Ctrl+Alt+A`

在资源管理器里选中文件，按 `Ctrl+Alt+A`，再按数字键 `1`–`9` 发给对应设备；也可以用 `↑` `↓` 选、`Enter` 发。没选文件时，会先弹出选文件的窗口。`Ctrl+C` 复制设备 IP，`Esc` 关闭。

### 剪贴板互通

在「本机」页打开「剪贴板互通」，开关旁边会弹出一个二维码：

- **手机、Mac 读这台电脑的剪贴板**：用登录了 Tailscale 的手机扫码，打开的网页上就是这台电脑剪贴板里的字，点「复制」拿走。在 Safari 里「添加到主屏幕」，以后点图标就行。
- **iPhone 把剪贴板发过来**：在同一个网页上点「把我的剪贴板发过去」，或者用下面的快捷指令，内容直接进这台电脑的剪贴板。
- **这台电脑读 Mac 的剪贴板**：按 `Win+Alt+V`，或者在星图上点 Mac、选「读剪贴板」。用的是 `~/.ssh/config` 里指向这台 Mac 的别名，Mac 要开着「远程登录」。iPhone 和 Mac 登录同一个 Apple 账号、开着接力时，iPhone 上刚复制的东西也能经 Mac 读过来。

想按一下就拿到：在 iPhone 的「快捷指令」里新建一个，加「获取 URL 内容」（网址是二维码里的网址后面加上 `raw`）和「拷贝至剪贴板」，再到「设置 → 辅助功能 → 触控 → 轻点背面」或「设置 → 操作按钮」里选它。反过来发给电脑：「获取剪贴板」→「获取 URL 内容」，方法选 POST，请求体选「文件」并放进剪贴板。

只有登录同一个 Tailscale 账号的设备能读，不走 Funnel，不公开到互联网；每次都是现读，不存盘；每被读一次，这台电脑弹一条通知，星图上光点流一下。密码管理器标了「别同步」的内容和 Tailscale 令牌不给。iPhone 不允许别的程序在后台读它的剪贴板，所以从手机往电脑发总要点一下。

### 其他几页

- **分享**：`tailscale serve` / `funnel` 上挂着的东西排成两圈——里圈只有自己的设备能打开，外圈所有人都能打开。把文件或文件夹拖进来，就生成一个只给自己设备看的链接和二维码；可以临时公开到互联网 15 分钟、1 小时或 1 天，到点自动关。背后的程序停了的分享会标红，点开能看到断在哪一环。
- **网络**：每台设备是直连还是经中继、延迟多少、正在传多快；右边是体检（UDP 打洞、IPv6、NAT 类型、端口映射、最近的中继、版本、密钥最早到期）、要处理的问题和流量走了哪条路。
- **云盘**：Taildrive。没开通时点「一键开通」：TailOrbit 打开 Tailscale 后台的密钥页，你点 Generate access token、再点 Copy，它从剪贴板认出这把令牌，在访问规则里加上 Taildrive 要的两条（原有的规则和注释不动），然后立刻作废令牌、清掉剪贴板。不想交出令牌就点「自己改规则」：要加的两段已经复制好，规则页也替你打开了，贴进去保存即可。开通后把文件夹拖进来就共享给自己的其他设备，也能直接打开别的设备共享的文件夹；iPhone、iPad 不支持 Taildrive，会跳过。
- **本机**：这台电脑的名字、域名、IP、账号和版本，开机自启，剪贴板互通，界面语言，出口节点，常用开关（`tailscale set`）。

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

TailOrbit 平时不连任何外部服务器，能交给 Tailscale 官方命令行的都交给它。唯一的例外是云盘「一键开通」：那一次会拿你给的令牌访问 Tailscale 官方后台接口 `api.tailscale.com`。

| 做什么 | 调用 |
| --- | --- |
| 列出能收文件的设备 | `tailscale file cp --targets`，`tailscale status --json` |
| 判断直连还是中继 | `tailscale ping --c 3 --timeout 2s <IP>` |
| 发送 | `tailscale file cp --verbose <文件…> <IP>:` |
| 发送、收件进度 | `tailscale debug watch-ipn` 里的 `OutgoingFiles`、`IncomingFiles` |
| 认出是谁发来的 | 连到本机收件端口（PeerAPI）的地址；看不到时看实时事件里各设备发来的字节数 |
| 分享 | `tailscale serve` / `tailscale funnel`；文件由 TailOrbit 在 `127.0.0.1` 上的小服务提供，Tailscale 只负责转发 |
| 网络体检、流量 | `tailscale netcheck --format json`，`tailscale metrics print` |
| 云盘 | `tailscale drive list / share / unshare`；别的设备的共享读 Tailscale 自带的 WebDAV（`100.100.100.100:8080`） |
| 云盘一键开通 | `api.tailscale.com` 的访问规则接口：读出来，只插两条，带 ETag 写回（期间别人改过就不写）；最后调接口作废这把令牌 |
| 本机设置 | `tailscale get --json`，`tailscale set` |
| 剪贴板互通 | `tailscale serve` 在 7443 端口把 `/c/<随机码>` 转发到 TailOrbit 在 `127.0.0.1` 上的小服务（只在自己的设备之间）；按 Tailscale 带上的访客账号放行；读 Mac 用 `ssh <别名> pbpaste` |
| 设备菜单的「网页」 | 设备在线时提前敲一下它的几个常见网页端口（443、80、8443、8080、3000…），结果记 2 分钟，点开菜单不用等 |

记录都只在本机 `%LOCALAPPDATA%\TailOrbit\` 里：`history.jsonl`（发出和收到的，只追加）、`shares.json`（分享）、`settings.json`（掉线提醒、界面语言、剪贴板互通开没开和网址里的随机码）、`error.log`，不会上传到任何地方。剪贴板的内容不记录。

### 说明

- 界面有简体中文、繁體中文、English、日本語、한국어、Deutsch、Français、Español 八种，默认跟随系统语言，可以在「本机」页换。分享出去的网页按访客浏览器的语言显示。
- 这是个人项目，与 Tailscale Inc. 没有关联，也没有得到它的认可。Tailscale 和 Taildrop 是 Tailscale Inc. 的商标。
- 代码用 [MIT 协议](LICENSE)。图标来自微软的 Fluent UI System Icons（MIT 协议），见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## English

TailOrbit is a small Windows companion for [Tailscale](https://tailscale.com). It lays your devices out as a star map: this PC sits in the middle and every other device sits on one of three rings, depending on how it is connected. Drop a file on a device and it goes out over Taildrop; progress and the result show up right on the map.

Five pages sit at the top of the window: **Orbit** (the map: send, receive, connect), **Share**, **Network**, **Drive** and **This PC**. `Ctrl+1`–`5` switches between them.

### The map

- **Inner ring (solid)**: direct connection.
- **Middle ring (dashed)**: through a DERP relay, or not probed yet.
- **Outer ring (dotted, hollow)**: offline; it can't receive files.

When the window opens, TailOrbit runs `tailscale ping` once against each online device to tell direct from relayed, and keeps the answer for 5 minutes. A device found to be direct slides along its line into the inner ring.

### Sending

- **Drag and drop**: drag files or folders into the window and release over an online device. An offline device shakes its head and nothing is sent.
- **Click**: click a device, choose "send file" in its menu, pick files, and they are sent.
- **Folders** are zipped first; the temporary zip is deleted afterwards.
- **While sending**, a progress ring wraps the device and dots flow out from the center. Hover the device to get an ×; click it to cancel.
- **When done**, the device turns green with a check mark. On failure the line turns red and the device shakes.

### Receiving

When another device sends you files, dots flow from that device into the center while a progress ring and percentage run on this PC. When it finishes you get a check mark, and the file shows up at the bottom left with an inbox badge (lit until you open it).

### Device menu

Click a device and a ring of buttons pops out; a label next to them says what the one under the pointer does:

- **Terminal**: opens `ssh` with the alias from `~/.ssh/config` that points at this device (in Windows Terminal when installed). Without an alias, Linux devices and devices running Tailscale SSH use their domain name.
- **Read clipboard**: Macs only. Brings the text on the Mac's clipboard to this PC (see Shared clipboard below).
- **Web**: shown only when the device has a common web port open; opens it in your browser.
- **Send file** and **copy domain name**.
- **Offline alert**: once on, you get a Windows notification when the device goes offline and when it comes back (checked every 30 seconds), and the device wears a small bell.

### More

- Click a device name to copy its Tailscale IP.
- Click this PC in the middle to open your Downloads folder, where Tailscale on Windows saves received files.
- Bottom left are the last 5 files sent or received: click one to show it in File Explorer, drag it onto a device to send it again, hover to see why it failed.
- Closing the window only hides it to the tray: left-click the tray icon to bring it back, right-click it to quit.

### Quick panel `Ctrl+Alt+A`

Select files in File Explorer, press `Ctrl+Alt+A`, then press `1`–`9` to send them to that device, or use `↑` `↓` and `Enter`. With nothing selected you get a file picker first. `Ctrl+C` copies the device IP and `Esc` closes the panel.

### Shared clipboard

Turn on **Shared clipboard** on the **This PC** page and a QR code pops up next to the switch:

- **Your phone or Mac reads this PC's clipboard**: scan the code with a phone signed in to Tailscale. The page shows the text on this PC's clipboard; tap **Copy**. Add it to your Home Screen from Safari and next time it's one tap.
- **Your iPhone sends its clipboard**: tap **Send my clipboard** on the same page, or use the shortcut below. The text goes straight onto this PC's clipboard.
- **This PC reads your Mac's clipboard**: press `Win+Alt+V`, or click the Mac on the map and choose **Read clipboard**. It uses the alias in `~/.ssh/config` that points at the Mac, so Remote Login must be on. If your iPhone and Mac share an Apple Account with Handoff on, what you just copied on the iPhone comes through the Mac as well.

For a single press: in Shortcuts on the iPhone, add **Get Contents of URL** (the address from the QR code with `raw` added at the end) and **Copy to Clipboard**, then choose the shortcut under Settings → Accessibility → Touch → Back Tap, or for the Action button. To send the other way: **Get Clipboard** → **Get Contents of URL** with Method set to POST and Request Body set to File, holding the clipboard.

Only devices signed in to the same Tailscale account can read it. Nothing goes through Funnel or the internet, the clipboard is read fresh for each request and never stored, and every read pops a notification on this PC and sends a pulse across the map. Text a password manager marks as not-to-sync and Tailscale keys are never handed out. iOS doesn't let other apps read its clipboard in the background, so sending from the phone always takes a tap.

### The other pages

- **Share**: whatever is on `tailscale serve` / `funnel`, drawn as two rings: the inner one only your devices can open, the outer one anyone can. Drop a file or folder in to get a link and QR code that only your devices can open; you can make it public on the internet for 15 minutes, 1 hour or 1 day, and it closes itself when time is up. Shares whose backend program has stopped are marked red; open one to see where the chain breaks.
- **Network**: for every device, direct or relayed, latency, and current speed. On the right: a health check (UDP hole punching, IPv6, NAT type, port mapping, nearest relay, version, earliest key expiry), things that need attention, and which paths your traffic took.
- **Drive**: Taildrive. If it isn't enabled yet, click **Turn on Drive**: TailOrbit opens the keys page of the Tailscale admin console, you click Generate access token and then Copy, and TailOrbit picks the token up from the clipboard, adds the two entries Taildrive needs to your access rules (your existing rules and comments stay untouched), then revokes the token and clears the clipboard right away. If you'd rather not hand over a token, choose **Edit access controls yourself**: the two snippets are copied for you and the rules page opens, so you only paste and save. Once enabled, drop a folder in to share it with your other devices, and open folders that your other devices share. iPhone and iPad don't support Taildrive and are skipped.
- **This PC**: this computer's name, domain, IP, account and version, start with Windows, shared clipboard, interface language, the exit node, and common switches (`tailscale set`).

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

TailOrbit normally talks to no outside server; whatever the official Tailscale CLI can do, it leaves to the CLI. The one exception is the one-click Drive setup, which uses the token you give it to call the official Tailscale API at `api.tailscale.com` once.

| What | Call |
| --- | --- |
| List devices that can receive files | `tailscale file cp --targets`, `tailscale status --json` |
| Tell direct from relayed | `tailscale ping --c 3 --timeout 2s <IP>` |
| Send | `tailscale file cp --verbose <files…> <IP>:` |
| Sending and receiving progress | `OutgoingFiles` and `IncomingFiles` from `tailscale debug watch-ipn` |
| Tell who is sending | the address connected to this PC's receiving port (PeerAPI); failing that, which device's byte count jumps in the live events |
| Share | `tailscale serve` / `tailscale funnel`; files are served by a small TailOrbit server on `127.0.0.1` and Tailscale only forwards to it |
| Network health and traffic | `tailscale netcheck --format json`, `tailscale metrics print` |
| Drive | `tailscale drive list / share / unshare`; other devices' shares come from Tailscale's built-in WebDAV (`100.100.100.100:8080`) |
| One-click Drive setup | the access-rules endpoint of `api.tailscale.com`: read, insert two entries, write back with the ETag (nothing is written if someone changed the rules in between); then revoke the token through the API |
| This PC | `tailscale get --json`, `tailscale set` |
| Shared clipboard | `tailscale serve` on port 7443 forwards `/c/<random code>` to TailOrbit's small server on `127.0.0.1`, between your own devices only; requests are let in by the visitor account Tailscale attaches. Reading a Mac uses `ssh <alias> pbpaste` |
| "Web" in the device menu | while a device is online, a quick connect to a few of its common web ports (443, 80, 8443, 8080, 3000…), remembered for 2 minutes so the menu opens without waiting |

Everything stays in `%LOCALAPPDATA%\TailOrbit\` on your PC: `history.jsonl` (sent and received, append-only), `shares.json` (shares), `settings.json` (offline alerts, interface language, whether the shared clipboard is on and the random code in its address) and `error.log`. Nothing is uploaded anywhere, and clipboard contents are never recorded.

### Notes

- The interface comes in Simplified Chinese, Traditional Chinese, English, Japanese, Korean, German, French and Spanish. It follows your Windows language by default and can be changed on the This PC page. Shared web pages follow the visitor's browser language.
- This is a personal project, not affiliated with or endorsed by Tailscale Inc. Tailscale and Taildrop are trademarks of Tailscale Inc.
- The code is under the [MIT License](LICENSE). The icons are Microsoft's Fluent UI System Icons (MIT License); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
