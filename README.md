<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/icon-dark.png"><img src="docs/icon.png" width="88" alt="TailOrbit"></picture></p>

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

TailOrbit 是配合 [Tailscale](https://tailscale.com) 用的一个 Windows 小工具。打开它，你的设备会围着这台电脑排成一张星图；把文件拖到哪台设备上，就用 Taildrop 发给哪台。

窗口顶上有六页：星网、分享、网络、云盘、本机、设置，用 `Ctrl+1` 到 `Ctrl+6` 切换。

### 发文件、收文件

星图有三圈。里圈是直连的设备，中圈是走中继的（或者还没测出来），外圈是离线的。每次打开窗口，TailOrbit 会挨个 ping 一下在线的设备，测出是直连的会自己滑进里圈。

发文件有三种办法：

- 把文件拖进窗口，对准一台设备松手；
- 点一台设备，选「发送文件」；
- 在资源管理器里选中文件，按 `Ctrl+Alt+A`，再按设备前面的数字键。

文件夹会先打成 zip 再发。发送时设备外面转一圈进度，鼠标移上去会出现 ×，点了就取消；送到了设备变绿打勾，失败了连线变红。

离线的设备也能发。照样拖上去（或者点它选「发送文件」、在 `Ctrl+Alt+A` 里按它的数字），文件会先排着，设备旁边多一颗绕着它转的小卫星。等它上线，比如合着的笔记本打开了，TailOrbit 自动发过去：卫星落进设备，接着就是平常的发送。TailOrbit 只记文件在哪、不另存一份，所以发出去的是那时候的最新版本；文件要是被挪走或删了，这一批就记成没送到。不想发了，点那台设备选「取消发送」。排着的文件重启后还在，只是对方上线时这台电脑得开着、TailOrbit 也在运行。

别人发文件过来时，光点会从那台设备流进中间。收好的文件出现在左下角，点一下就在资源管理器里找到它，拖到别的设备上还能接着发。

### 点一下设备

点一台设备，四周会弹出一圈按钮：开终端（用你在 `~/.ssh/config` 里给它起的别名）、发文件、复制域名、在浏览器里打开它的网页（它开着网页端口才有）、读它的剪贴板（只有 Mac 有），还有「离线时提醒我」：打开后，它掉线和恢复时 Windows 都会弹个通知。

点设备的名字会复制它的 IP；点中间这台电脑会打开「下载」文件夹，Tailscale 收到的文件都放在那里。关掉窗口只是缩回托盘。右键托盘图标是一个小菜单：「发送文件」「已接收的文件」（找到最近收到的那个）、「终端」（指着它列出能连的设备），两个剪贴板开关也在这里，最下面是「退出」。

### 剪贴板

「设置」页上有两个跟剪贴板有关的开关。

**剪贴板互通**是给手机用的。打开后会弹出一个二维码，用登录了 Tailscale 的手机扫一下，网页上就是这台电脑剪贴板里的字，点「复制」就拿到了；点「发送我的剪贴板」，手机上的字就到了电脑上。

嫌扫码麻烦，可以在 iPhone 的「快捷指令」里做两个，再到「设置 → 辅助功能 → 触控 → 轻点背面」里绑上：

- 拿电脑的：「获取 URL 内容」（网址就是二维码里那个，末尾加上 `raw`）→「拷贝至剪贴板」。电脑剪贴板里是图片时这个网址拿不到字，中间最好加一个「如果…有任何值」，不然会把手机剪贴板清空。
- 发给电脑：「获取剪贴板」→「文本」→「获取 URL 内容」，方法选 POST，请求体选「文件」，放进上一步的文本。

**与 Mac 共享剪贴板**是给 Mac 用的。打开后，这台电脑和 Mac 就像在用同一个剪贴板：在哪边复制，两边都能粘贴。文字和图片（比如截图）都传，复制文件不传；超过 32 MB 的图片也不传，会弹个通知，这种用「发送文件」发。它用 `~/.ssh/config` 里 Mac 的别名连过去，所以 Mac 要打开「远程登录」，Mac 上不用装任何东西。开关旁边的小电脑亮着，说明连上了。Mac 合盖睡着时会断开，醒来后自动连回，中间复制过的东西会补过去。如果 iPhone 和 Mac 登录的是同一个 Apple 账号、也开着接力，iPhone 上复制的东西也会经 Mac 一起过来。

不想一直同步的话，也可以只在需要时按 `Win+Alt+V`，把 Mac 剪贴板里的字读到这台电脑上。

剪贴板只在你自己的设备之间走：只有登录同一个 Tailscale 账号的设备能读，不经过 Funnel，也不上互联网，内容不会存到硬盘上。密码也照样能传；在两台电脑之间传时它仍然标着是密码，不会进剪贴板历史。

### 其他几页

- **分享**：把文件或文件夹拖进来，会生成一个只有你自己的设备能打开的链接和二维码；需要的话可以公开到互联网 15 分钟、1 小时或 1 天，时间到了自动关。你用 `tailscale serve`、`funnel` 挂着的东西也都在这页，背后的程序停了会标红。
- **网络**：每台设备是直连还是中继、延迟多少、现在传得多快；右边是网络检测和需要处理的问题。
- **云盘**：就是 Tailscale 的 Taildrive。还没开通的话点「立即开通」，照提示在 Tailscale 后台生成一把令牌、点 Copy，TailOrbit 会替你在访问规则里加上需要的两条，用完马上把这把令牌作废。不想交出令牌也行，点「手动修改访问控制」，要加的内容已经替你复制好了。开通以后，把文件夹拖进来就共享给了自己的其他设备，别的设备共享的文件夹也能直接打开。
- **本机**：这台电脑在 Tailscale 里的名字、地址和账号，出口节点和常用开关。
- **设置**：界面用浅色、深色还是跟随系统；「开机时自动启动」、界面语言和上面说的两个剪贴板开关；检查更新。

### 安装

需要 Windows 10（1903 或更新）或 Windows 11。用的是系统自带的 .NET Framework 4.8，不用另装运行库。

到 [Releases](https://github.com/TinSB/TailOrbit/releases) 下载最新的 `TailOrbit-Setup-版本号.exe`，双击，点「安装」。不用管理员权限，程序装在 `%LOCALAPPDATA%\Programs\TailOrbit`，开始菜单里会有它，「开机时自动启动」默认打开。

电脑上还没装 Tailscale 的话，安装窗口里会多一个「同时安装 Tailscale」（默认打开）：TailOrbit 装好后从 Tailscale 官网下载对应处理器的安装包，确认是 Tailscale Inc. 签名、签名有效，再交给 Windows 安装（会弹一次管理员确认），装完打开 Tailscale 让你登录。这一步没装上也不影响 TailOrbit，主界面上同样有「安装 Tailscale」按钮。

安装程序没有数字签名，第一次运行时 Windows 可能提示「Windows 已保护你的电脑」，点「更多信息」，再点「仍要运行」就行。

出了新版，在「设置」页点「检查更新」，再点「立即更新」：TailOrbit 从 GitHub 下载新的安装程序，核对过大小和 SHA-256 后自己换成新版、重新打开，设置和收发记录都还在。打开「自动下载更新」的话，它每天自己看一次，有新版就在后台下好，等你点「重启并更新」。照旧下载新的安装程序再运行一次也行。

### 卸载

在「设置 → 应用」里找到 TailOrbit，点「卸载」。它在 Tailscale 上挂的分享和剪贴板互通会一起撤掉；设置和收发记录默认留着，打开「同时删除设置和传输记录」就一起删。

### 自己编译

不用安装程序也行，拿 Windows 自带的编译器编一下，不用装 Visual Studio：

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

用 `TailOrbit.exe --tray` 启动就只待在托盘里，「开机时自动启动」就是这么启动的。程序同时只会开一个，再运行一次只会把已经开着的窗口叫到前面。

`.\release.ps1` 打出 `dist\TailOrbit-Setup-版本号.exe`。安装程序加 `--quiet` 就不弹窗口直接装好（装好后待在托盘里，再加 `--open` 就打开主界面）；`TailOrbit.exe --uninstall --quiet` 不弹窗口卸载，再加 `--purge` 连设置和记录一起删。

图标有深浅两份，任务栏、托盘和开始菜单跟着 Windows 的深浅色换，都是 `make-icon.ps1` 画的：改了它再运行一次，会重新生成 `TailOrbit.ico` 和 `TailOrbit-light.ico`。

### 隐私

TailOrbit 自己不连任何外部服务器，能交给 Tailscale 命令行做的都交给它。例外只有三处：云盘的「立即开通」那一次会拿你给的令牌访问 Tailscale 官方的后台接口；没装 Tailscale、你点了安装时，会从 Tailscale 官网（`pkgs.tailscale.com`）下载它的安装包；检查更新时会访问 GitHub（`api.github.com` 和它的下载地址）。「自动下载更新」默认关着，不打开的话只有你点「检查更新」时才会连。

收发记录、排着要发的文件清单、分享列表和设置都只存在这台电脑的 `%LOCALAPPDATA%\TailOrbit\` 里，不会上传，剪贴板的内容也不记录。

<details>
<summary>具体调了哪些命令</summary>

| 做什么 | 怎么做 |
| --- | --- |
| 列出能收文件的设备 | `tailscale file cp --targets`、`tailscale status --json` |
| 判断直连还是中继 | `tailscale ping` |
| 发文件、看收发进度 | `tailscale file cp`；进度来自 `tailscale debug watch-ipn` |
| 等离线的设备上线 | 有排着的文件时每 15 秒看一次 `tailscale file cp --targets`，对方在线了照常用 `tailscale file cp` 发 |
| 分享 | `tailscale serve`、`tailscale funnel`；文件由 TailOrbit 在 `127.0.0.1` 上开的小服务提供，Tailscale 只负责转发 |
| 网络检测、流量 | `tailscale netcheck`、`tailscale metrics print` |
| 云盘 | `tailscale drive`；别的设备的共享通过 Tailscale 自带的 WebDAV（`100.100.100.100:8080`）读 |
| 开通云盘 | 用你的令牌读出访问规则，只加两条，写回前确认这期间没人改过，最后把令牌作废 |
| 本机设置 | `tailscale get`、`tailscale set` |
| 剪贴板互通 | `tailscale serve` 把 7443 端口上一个带随机码的地址转给本机的小服务，只放行你自己账号的设备 |
| 读取 Mac 剪贴板、与 Mac 共享剪贴板 | 经 `ssh` 在 Mac 上跑 `pbpaste` 和一个小脚本；Windows 这边靠系统发来的“剪贴板变了”的通知。两边写进去时都带个记号，不会来回打转 |
| 设备菜单里的「网页」 | 设备在线时先试连几个常见的网页端口，菜单一打开就知道有没有 |
| 安装 Tailscale | 从 `pkgs.tailscale.com` 下载对应处理器的 MSI，用 Windows 验证 Authenticode 签名并确认签名者是 Tailscale Inc.，再用 `msiexec` 安装 |
| 给桌面小组件用的本机接口 | 只听 `127.0.0.1:46214`，每次启动换一把随机令牌，存在 `%LOCALAPPDATA%\TailOrbit\island.json`；能列设备、发文件、看收发进度 |
| 检查更新 | 从 `api.github.com` 读最新发布的版本；安装包从 GitHub 下载到 `%LOCALAPPDATA%\TailOrbit\update\`，大小和 SHA-256 要和发布页上写的一样、版本号也对得上才装，装的时候不弹窗口运行它 |

</details>

### 其他

界面有简体中文、繁體中文、English、日本語、한국어、Deutsch、Français、Español 八种，默认跟着系统语言，在「设置」页可以换；分享出去的网页按访问者浏览器的语言显示。

这是个人项目，和 Tailscale 公司没有关系，也没有得到它的认可。Tailscale 和 Taildrop 是 Tailscale Inc. 的商标。代码用 [MIT 协议](LICENSE)；图标来自微软的 Fluent UI System Icons（同样是 MIT 协议），见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## English

TailOrbit is a small Windows app for [Tailscale](https://tailscale.com). It lays your devices out as a star map around this PC. Drag a file onto a device and it goes there over Taildrop.

The window has six pages along the top: Orbit, Share, Network, Drive, This PC and Settings. `Ctrl+1` to `Ctrl+6` switch between them.

### Sending and receiving

The map has three rings. Devices you reach directly sit on the inner ring, relayed ones (or ones not checked yet) on the middle ring, and offline ones on the outer ring. Each time the window opens, TailOrbit pings the online devices once, and any that turn out to be direct slide into the inner ring.

There are three ways to send:

- drag files into the window and drop them on a device;
- click a device and choose **Send files**;
- select files in File Explorer, press `Ctrl+Alt+A`, then press the number shown next to a device.

Folders get zipped first. While a file is on its way, a progress ring runs around the device; hover it and click the × to cancel. The device turns green when the file arrives, and the line turns red if it fails.

Offline devices can receive too. Drop files on one (or click it and choose **Send files**, or press its number in the `Ctrl+Alt+A` panel) and they wait in line, shown as a small satellite circling the device. When the device comes back online, say when you open a closed laptop, TailOrbit sends them automatically: the satellite drops into the device and the usual send begins. TailOrbit only remembers where the files are and doesn't copy them, so whatever is there at that moment is what gets sent; if a file was moved or deleted, that batch is marked as failed. Changed your mind? Click the device and choose **Cancel sending**. Waiting files survive a restart, but this PC has to be on, with TailOrbit running, when the other device comes online.

When someone sends you a file, dots flow from their device into the middle. Received files show up at the bottom left: click one to find it in File Explorer, or drag it onto another device to pass it on.

### Clicking a device

Click a device and a ring of buttons pops out around it: open a terminal (with the alias you gave it in `~/.ssh/config`), send files, copy its DNS name, open its web page (only when it has a web port open), read its clipboard (Macs only), and **Alert me when offline**, which makes Windows notify you when that device drops off and when it comes back.

Click a device's name to copy its IP. Click this PC in the middle to open Downloads, where Tailscale puts the files you receive. Closing the window only hides it in the tray. Right-click the tray icon for a small menu: **Send files**, **Received files** (shows the last file you received), **Terminal** (point at it to pick a device), the two clipboard switches, and **Exit** at the bottom.

### Clipboard

The Settings page has two clipboard switches.

**Shared clipboard** is for your phone. Turn it on and a QR code pops up. Scan it with a phone signed in to Tailscale and you get a page showing this PC's clipboard: tap **Copy** to take it, or **Send my clipboard** to put the phone's text on the PC.

If scanning gets old, make two shortcuts in the iPhone's Shortcuts app and put them on Back Tap (Settings → Accessibility → Touch → Back Tap):

- Get the PC's clipboard: **Get Contents of URL** (the address from the QR code with `raw` added at the end) → **Copy to Clipboard**. When the PC's clipboard holds an image, that address has no text, so put an **If … has any value** in between, or your phone's clipboard gets wiped.
- Send to the PC: **Get Clipboard** → **Text** → **Get Contents of URL**, with Method set to POST and Request Body set to File, holding the text.

**Sync clipboard with Mac** is for your Mac. Turn it on and this PC and the Mac act like one clipboard: copy on either, paste on both. Text and images (screenshots, say) come across; copied files don't. Images over 32 MB are skipped with a notification; send those with **Send files**. It connects with the alias for your Mac in `~/.ssh/config`, so Remote Login needs to be on, and nothing is installed on the Mac. The little laptop next to the switch lights up while it's connected. When the Mac sleeps the link drops; when it wakes, TailOrbit reconnects and passes along anything copied in between. If your iPhone and Mac share an Apple Account with Handoff on, whatever you copy on the iPhone comes through the Mac too.

If you'd rather not sync all the time, press `Win+Alt+V` whenever you want to pull the Mac's clipboard onto this PC.

The clipboard only travels between your own devices: only devices signed in to your Tailscale account can read it, nothing goes through Funnel or the internet, and nothing is written to disk. Passwords come along too; between the two computers they stay marked as passwords, so they don't end up in clipboard history.

### The other pages

- **Share**: drop a file or folder here to get a link and QR code that only your devices can open. If you need to, make it public on the internet for 15 minutes, an hour or a day; it closes itself when time is up. Anything you already serve with `tailscale serve` or `funnel` shows up here too, and turns red if the program behind it has stopped.
- **Network**: whether each device is direct or relayed, its latency and how fast it's transferring right now, plus a network check and anything that needs fixing.
- **Drive**: Tailscale's Taildrive. If it isn't set up yet, click **Enable Drive**, generate an access token in the Tailscale admin console when asked, and click Copy. TailOrbit adds the two entries Taildrive needs to your access rules and revokes the token right away. If you'd rather not hand over a token, choose **Edit access controls manually**; the text to paste is already copied for you. Once it's on, drop a folder in to share it with your other devices, and open the folders they share.
- **This PC**: this computer's name, addresses and account on Tailscale, the exit node and common switches.
- **Settings**: light, dark or follow Windows; **Start with Windows**, the interface language and the two clipboard switches; checking for updates.

### Install

You need Windows 10 (1903 or later) or Windows 11. It runs on the .NET Framework 4.8 that comes with Windows, so there's nothing else to install.

Download the latest `TailOrbit-Setup-<version>.exe` from [Releases](https://github.com/TinSB/TailOrbit/releases), double-click it and click **Install**. No admin rights needed: it goes into `%LOCALAPPDATA%\Programs\TailOrbit`, shows up in the Start menu, and starts with Windows unless you turn that off.

If Tailscale isn't installed yet, the installer adds an **Also install Tailscale** option (on by default). After installing TailOrbit, it downloads the installer for your processor from the official Tailscale website, checks that it carries a valid signature from Tailscale Inc., and hands it to Windows to install (you'll see one administrator prompt), then opens Tailscale so you can sign in. If that step fails, TailOrbit still works, and the main window offers the same **Install Tailscale** button.

The installer isn't code-signed, so the first time you run it Windows may say "Windows protected your PC". Click **More info**, then **Run anyway**.

To update, click **Check for updates** on the Settings page, then **Update now**. TailOrbit downloads the new installer from GitHub, checks its size and SHA-256, replaces itself with the new version and reopens; your settings and history stay. Turn on **Download updates automatically** and it checks once a day, downloads new versions in the background and waits for you to click **Restart to update**. Downloading the new installer and running it yourself still works too.

### Uninstall

In **Settings → Apps**, find TailOrbit and click **Uninstall**. The shares and shared clipboard it set up in Tailscale are taken down too. Your settings and transfer history are kept unless you turn on **Also delete settings and transfer history**.

### Build it yourself

You can skip the installer and build it with the compiler that ships with Windows; no Visual Studio needed:

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

Run `TailOrbit.exe --tray` to start in the tray only; that's how **Start with Windows** launches it. Only one copy runs at a time, and launching it again just brings the open window to the front.

`.\release.ps1` builds `dist\TailOrbit-Setup-<version>.exe`. Add `--quiet` to the installer to install without any window (it then starts in the tray; add `--open` to open the main window instead); `TailOrbit.exe --uninstall --quiet` removes it the same way, and `--purge` also deletes settings and history.

The icon comes in a dark and a light version; the taskbar, tray and Start menu switch along with Windows light or dark mode. Both are drawn by `make-icon.ps1`: edit it and run it again to regenerate `TailOrbit.ico` and `TailOrbit-light.ico`.

### Privacy

TailOrbit doesn't talk to any outside server; it leaves everything it can to Tailscale's own command line. There are three exceptions: Drive setup uses the token you give it to call Tailscale's admin API once; if Tailscale isn't installed and you choose to install it, the installer is downloaded from the official Tailscale website (`pkgs.tailscale.com`); and checking for updates contacts GitHub (`api.github.com` and its download servers). **Download updates automatically** is off by default, so unless you turn it on, that only happens when you click **Check for updates**.

Your history, the list of files waiting to be sent, shares and settings stay in `%LOCALAPPDATA%\TailOrbit\` on this PC and are never uploaded. Clipboard contents aren't recorded either.

<details>
<summary>What it runs under the hood</summary>

| What | How |
| --- | --- |
| List devices that can receive files | `tailscale file cp --targets`, `tailscale status --json` |
| Tell direct from relayed | `tailscale ping` |
| Send files and track progress | `tailscale file cp`; progress comes from `tailscale debug watch-ipn` |
| Wait for an offline device | while files are waiting, checks `tailscale file cp --targets` every 15 seconds and sends with `tailscale file cp` once the device is online |
| Share | `tailscale serve`, `tailscale funnel`; files are served by a small TailOrbit server on `127.0.0.1`, and Tailscale only forwards to it |
| Network check and traffic | `tailscale netcheck`, `tailscale metrics print` |
| Drive | `tailscale drive`; other devices' shares are read through Tailscale's built-in WebDAV (`100.100.100.100:8080`) |
| Drive setup | reads your access rules with the token, adds two entries, checks that nobody changed them in the meantime before writing back, then revokes the token |
| This PC | `tailscale get`, `tailscale set` |
| Shared clipboard | `tailscale serve` forwards an address with a random code on port 7443 to the small local server, and only lets in devices on your own account |
| Reading the Mac, syncing with the Mac | runs `pbpaste` and a small script on the Mac over `ssh`; on Windows it listens for the system's "clipboard changed" notice. Both sides tag what they write, so nothing bounces back and forth |
| "Web" in the device menu | tries a few common web ports while the device is online, so the menu knows right away |
| Installing Tailscale | downloads the MSI for your processor from `pkgs.tailscale.com`, has Windows verify its Authenticode signature and checks the signer is Tailscale Inc., then installs it with `msiexec` |
| Local API for desktop widgets | listens only on `127.0.0.1:46214`, with a new random token each launch saved in `%LOCALAPPDATA%\TailOrbit\island.json`; lists devices, sends files, streams transfer progress |
| Updates | reads the latest release from `api.github.com`; the installer is downloaded from GitHub into `%LOCALAPPDATA%\TailOrbit\update\` and only used if its size and SHA-256 match the release page and its version number matches; it then runs without a window |

</details>

### Notes

The interface comes in Simplified Chinese, Traditional Chinese, English, Japanese, Korean, German, French and Spanish. It follows your Windows language and can be changed on the Settings page; shared web pages follow the visitor's browser language.

This is a personal project, not affiliated with or endorsed by Tailscale Inc. Tailscale and Taildrop are trademarks of Tailscale Inc. The code is under the [MIT License](LICENSE); the icons are Microsoft's Fluent UI System Icons (also MIT), see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
