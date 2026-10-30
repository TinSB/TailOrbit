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

窗口顶上有五页：星网、分享、网络、云盘、设置，用 `Ctrl+1` 到 `Ctrl+5` 切换。

### 发文件、收文件

星图有三圈。里圈是直连的设备，中圈是走中继的（或者还没测出来），外圈是离线的。每次打开窗口，TailOrbit 会挨个 ping 一下在线的设备，测出是直连的会自己滑进里圈。

发文件有三种办法：

- 把文件拖进窗口，对准一台设备松手；
- 点一台设备，选「发送文件」；
- 在资源管理器里选中文件，按 `Ctrl+Alt+A`（或者右键 →「发送到」→「TailOrbit」，Windows 11 在「显示更多选项」里），再按设备前面的数字键。

文件夹会先打成 zip 再发。发送时设备外面转一圈进度，鼠标移上去会出现 ×，点了就取消；送到了设备变绿打勾，失败了连线变红。

离线的设备也能发。照样拖上去（或者点它选「发送文件」、在 `Ctrl+Alt+A` 里按它的数字），文件会先排着，设备旁边多一颗绕着它转的小卫星。等它上线，比如合着的笔记本打开了，TailOrbit 自动发过去：卫星落进设备，接着就是平常的发送。TailOrbit 只记文件在哪、不另存一份，所以发出去的是那时候的最新版本；文件要是被挪走或删了，这一批就记成没送到。不想发了，点那台设备选「取消发送」。排着的文件重启后还在，只是对方上线时这台电脑得开着、TailOrbit 也在运行。

别人发文件过来时，光点会从那台设备流进中间。收好的文件出现在左下角，点一下就在资源管理器里找到它，拖到别的设备上还能接着发。收到的文件放在「下载」里：平常是 Tailscale 自己的托盘程序放过去的；它没开着时由 TailOrbit 来放，同名的文件不会被覆盖，新来的会叫「名字 (1)」，放好后弹个通知。

### 点一下设备

点一台设备，四周会弹出一圈按钮：开终端（用你在 `~/.ssh/config` 里给它起的别名）、远程桌面（只有 Windows 设备有，对方要开着远程桌面）、发文件、复制域名、在浏览器里打开它的网页（它开着网页端口才有）、读它的剪贴板（只有 Mac 有），还有「离线时提醒我」：打开后，它掉线和恢复时 Windows 都会弹个通知。

点设备的名字会复制它的 IP；点中间这台电脑会打开「下载」文件夹，Tailscale 收到的文件都放在那里。关掉窗口只是缩回托盘。右键托盘图标是一个小菜单：最上面是「连接 Tailscale」的开关，下面是「打开主界面」「发送文件」「已接收的文件」（找到最近收到的那个）、「终端」（指着它列出能连的设备），两个剪贴板开关也在这里，最下面是「退出」。

### 连接和账号

TailOrbit 可以代替 Tailscale 自己的托盘图标：

- **连接、断开**：托盘菜单最上面的开关，或者「设置」页左边「状态」那一行。断开以后星网和「网络」页会写「已断开」，点「连接」就连回来，设置都不变。
- **登录**：没登录、或者这台电脑的密钥过期了，那个开关会换成「登录 Tailscale」「重新登录 Tailscale」，点了用浏览器打开 Tailscale 的登录页，登录好就自动连上。重新登录还是原来的账号，之前的设置都还在。
- **换账号**：点「设置」页的「账号」，列出这台电脑登录过的账号，当前的打着勾，点别的就换过去；也可以「添加账号」（登录另一个账号，没登录完就换回原来那个），或者「退出登录」。退出登录会让这台电脑离开 Tailscale 网络，分享、剪贴板互通和收听都会停，所以要再点一下确认。
- **提醒**：Tailscale 自己的托盘程序没开着时，TailOrbit 替它弹通知：需要登录了、这台电脑的密钥 7 天内到期（一天最多一次）、Tailscale 报的问题持续了一分钟以上（同一条只报一次）、Tailscale 被断开或者它的后台服务停了。它的托盘程序开着就不重复弹。

### 剪贴板

「设置」页上有两个跟剪贴板有关的开关。

**剪贴板互通**是给手机用的。打开后会弹出一个二维码，用登录了 Tailscale 的手机扫一下，网页上就是这台电脑剪贴板里的字，点「复制」就拿到了；点「发送我的剪贴板」，手机上的字就到了电脑上。

嫌扫码麻烦，可以在 iPhone 的「快捷指令」里做两个，再到「设置 → 辅助功能 → 触控 → 轻点背面」里绑上：

- 拿电脑的：「获取 URL 内容」（网址就是二维码里那个，末尾加上 `raw`）→「拷贝至剪贴板」。电脑剪贴板里是图片时这个网址拿不到字，中间最好加一个「如果…有任何值」，不然会把手机剪贴板清空。
- 发给电脑：「获取剪贴板」→「文本」→「获取 URL 内容」，方法选 POST，请求体选「文件」，放进上一步的文本。

装了配套的 iPhone App（见「用手机听电脑的声音」）就不用扫码、也不用做快捷指令：App 在后台连着这台电脑，这边一复制，文字或图片就放进 iPhone 的剪贴板，不用打开 App，直接粘贴就行。反过来，在 iPhone 上复制后打开 App 点「粘贴」就发过来了：iOS 不许 App 在后台读剪贴板，这一步得点一下。密码传到 iPhone 上只留在那台手机里，两分钟后自动清掉。

**与 Mac 共享剪贴板**是给 Mac 用的。打开后，这台电脑和 Mac 就像在用同一个剪贴板：在哪边复制，两边都能粘贴。文字和图片（比如截图）都传，复制文件不传；超过 32 MB 的图片也不传，会弹个通知，这种用「发送文件」发。它用 `~/.ssh/config` 里 Mac 的别名连过去，所以 Mac 要打开「远程登录」，Mac 上不用装任何东西。开关旁边的小电脑亮着，说明连上了。Mac 合盖睡着时会断开，醒来后自动连回，中间复制过的东西会补过去。如果 iPhone 和 Mac 登录的是同一个 Apple 账号、也开着接力，iPhone 上复制的东西也会经 Mac 一起过来。

不想一直同步的话，也可以只在需要时按 `Win+Alt+V`，把 Mac 剪贴板里的字读到这台电脑上。

剪贴板只在你自己的设备之间走：只有登录同一个 Tailscale 账号的设备能读，不经过 Funnel，也不上互联网，内容不会存到硬盘上。密码也照样能传；在两台电脑之间传时它仍然标着是密码，不会进剪贴板历史。Tailscale 的访问令牌例外：不会同步到 Mac，也不会推到手机上。

### 用手机听电脑的声音

「设置」页的「在手机上收听本机声音」打开后也会弹出一个二维码。用登录了 Tailscale 的手机扫一下，点中间的圆钮，手机（和连着它的耳机，比如 AirPods）就开始放这台电脑正在放的声音。声音不压缩，只攒几十毫秒就放，网络抖了才多攒一点；圆钮下面的「缓冲」就是现在攒了多少。蓝牙耳机自己还会再慢一些。

电脑这边照常出声。声音是 16 位立体声，大约每秒 190 KB，用手机流量听一小时约 0.7 GB。和剪贴板一样，只有你自己账号的设备能打开，不经过 Funnel。

不想每次扫码、点按钮，可以装配套的 iPhone App（在 `ios/`，要用 Mac 自己编译装上，见「自己编译」）。打开一次、允许定位以后，它就在后台等着：手机连着 Wi-Fi、戴着耳机时，电脑一出声就自动从耳机里放出来，手机上正在放的歌只是被压低一点、不会停；电脑安静 8 秒，它就停下、把耳机让出来。摘了耳机、离开 Wi-Fi 就断开，不费电也不耗流量。它也能和电脑共用剪贴板，见「剪贴板」。App 被从后台划掉或手机重启以后，要再打开一次。

### 网络

「网络」页左边是你的设备，标着每台是直连还是中继、延迟多少、现在传得多快。点一台设备会弹出它的卡片：现在走的哪条路、近 24 小时的延迟曲线（实线是直连，虚线是走中继）、近 30 天每天的流量。

右边最上面是要处理的事：Tailscale 报的问题、打不开的分享，还有本机共享的出口节点或局域网还没批准、哪台设备的密钥两周内到期、哪台设备离线超过一周。每条旁边就有「批准」「关闭到期」或「删除」，删除要再点一下确认。

下面是网络检测：UDP 穿透、IPv6、NAT 类型、最近的中继这些，加上 DNS 能不能解析 Tailscale 里的名字，以及这台电脑有哪些端口其他设备连得进来，点开能看到是哪些程序、哪些端口。Windows 防火墙默认不拦从 Tailscale 来的连接，挡人的是管理控制台里的访问规则，所以这里列的是规则放行时其他设备就能连上的东西。

最下面是流量，可以看本次启动、今天、近 7 天或近 30 天，分直连和中继。TailOrbit 每分钟记一次各设备的流量和延迟，记录只存在这台电脑上，所以「今天」和更长的范围从装上这一版才开始有。正在用的设备改走中继、或者延迟比平时高出很多时，会弹个通知；不想要就在「设置」页关掉「连接变差时提醒」。

「批准」「关闭到期」「删除」要先连上管理接口：在「设置」页点「管理接口」，照提示在 Tailscale 后台生成一把访问令牌、点 Copy，TailOrbit 会自己拿走并清空剪贴板。令牌加密存在这台电脑上，只发给 Tailscale，过期了再连一次就行。不用了点「断开」，再到后台的 Keys 页面把它作废。不连也没关系，这些按钮会换成「前往管理控制台」，到网页上自己点。

### 其他几页

- **分享**：把文件或文件夹拖进来，会生成一个只有你自己的设备能打开的链接和二维码；需要的话可以公开到互联网 15 分钟、1 小时或 1 天，时间到了自动关。你用 `tailscale serve`、`funnel` 挂着的东西也都在这页，背后的程序停了会标红。
- **云盘**：就是 Tailscale 的 Taildrive。还没开通的话点「立即开通」，照提示在 Tailscale 后台生成一把令牌、点 Copy，TailOrbit 会替你在访问规则里加上需要的两条，用完马上把这把令牌作废。不想交出令牌也行，点「手动修改访问控制」，要加的内容已经替你复制好了。开通以后，把文件夹拖进来就共享给了自己的其他设备，别的设备共享的文件夹也能直接打开。
- **设置**：左边是这台电脑在 Tailscale 里的名字、地址、连接状态、账号和 Tailscale 版本。点名字就能改名；这台电脑要是在管理控制台里改过名，得连着管理接口才改得动。开着剪贴板互通或收听本机声音时，手机上存的网址会跟着名字变，所以会先提醒一次，再按一次 Enter 才改。有新版本时下面会出现「更新 Tailscale 到 …」，点一下、在管理员确认里点「是」就装好。右边从上往下是出口节点（Tailscale 推荐的那台标着「推荐」）、Tailscale 的常用开关和「网络」里说的管理接口、界面用浅色深色还是跟随系统、「开机时自动启动」、界面语言和「连接变差时提醒」、上面说的两个剪贴板开关和收听本机声音，最后是检查更新。常用开关里的「允许其他设备通过本机访问局域网」会把这台电脑所在的局域网（比如家里的路由器、打印机）共享给你的其他设备，要在管理控制台批准一次，连着管理接口的话 TailOrbit 会顺手批准。出口节点可以设成「在不信任的网络上自动使用」：连上咖啡店、机场这类网络时自动改走你选的设备；把家里、公司的网络设成「信任」，回到这些网络时它自己关掉。

### 安装

需要 Windows 10（1903 或更新）或 Windows 11。用的是系统自带的 .NET Framework 4.8，不用另装运行库。

到 [Releases](https://github.com/TinSB/TailOrbit/releases) 下载最新的 `TailOrbit-Setup-版本号.exe`，双击，点「安装」。不用管理员权限，程序装在 `%LOCALAPPDATA%\Programs\TailOrbit`，开始菜单和资源管理器右键的「发送到」里会有它，「开机时自动启动」默认打开。

电脑上还没装 Tailscale 的话，安装窗口里会多一个「同时安装 Tailscale」（默认打开）：TailOrbit 装好后从 Tailscale 官网下载对应处理器的安装包，确认是 Tailscale Inc. 签名、签名有效，再交给 Windows 安装（会弹一次管理员确认），装完打开 Tailscale 让你登录。这一步没装上也不影响 TailOrbit，主界面上同样有「安装 Tailscale」按钮。

安装程序没有数字签名，第一次运行时 Windows 可能提示「Windows 已保护你的电脑」，点「更多信息」，再点「仍要运行」就行。

出了新版，在「设置」页点「检查更新」，再点「立即更新」：TailOrbit 从 GitHub 下载新的安装程序，核对过大小和 SHA-256 后自己换成新版、重新打开，设置和收发记录都还在。打开「自动下载更新」的话，它每天自己看一次，有新版就在后台下好，等你点「重启并更新」。照旧下载新的安装程序再运行一次也行。

### 卸载

在「设置 → 应用」里找到 TailOrbit，点「卸载」。它在 Tailscale 上挂的分享、剪贴板互通和收听本机声音会一起撤掉，开始菜单和「发送到」里的那一项也删掉；设置、收发记录和管理接口的令牌默认留着，打开「同时删除设置和传输记录」就一起删。令牌不再用的话，记得到 Tailscale 后台的 Keys 页面把它作废。

### 自己编译

不用安装程序也行，拿 Windows 自带的编译器编一下，不用装 Visual Studio：

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

用 `TailOrbit.exe --tray` 启动就只待在托盘里，「开机时自动启动」就是这么启动的。程序同时只会开一个，再运行一次只会把已经开着的窗口叫到前面；`TailOrbit.exe --send 文件…` 会带着这些文件弹出快捷面板（「发送到」就是这么启动的）。

`.\release.ps1` 打出 `dist\TailOrbit-Setup-版本号.exe`。安装程序加 `--quiet` 就不弹窗口直接装好（装好后待在托盘里，再加 `--open` 就打开主界面）；`TailOrbit.exe --uninstall --quiet` 不弹窗口卸载，再加 `--purge` 连设置和记录一起删。

图标有深浅两份，任务栏、托盘和开始菜单跟着 Windows 的深浅色换，都是 `make-icon.ps1` 画的：改了它再运行一次，会重新生成 `TailOrbit.ico` 和 `TailOrbit-light.ico`。

iPhone App 在 `ios/` 里，要一台装了 Xcode 和 [XcodeGen](https://github.com/yonaskolb/XcodeGen) 的 Mac。在 `ios/` 里新建 `Local.xcconfig`，写上你的签名团队、收听网址（就是设置页给的那个；xcconfig 里 `//` 是注释，要写成 `https:/$()/...`）和手机的设备编号，格式见 `Config.xcconfig` 开头。然后在 Mac 上运行 `./mac.sh devinstall` 编译并装到手机（签名要在 Mac 登录着的桌面里进行，SSH 里会失败），`./mac.sh sim` 在模拟器里试。App 图标也由 `make-icon.ps1 -Ios` 生成。

### 隐私

TailOrbit 自己不连任何外部服务器，能交给 Tailscale 命令行做的都交给它。例外只有四处：云盘的「立即开通」那一次会拿你给的令牌访问 Tailscale 官方的后台接口；连了管理接口的话，连上时核对一次令牌，之后只在你点「批准」「关闭到期」「删除」或改名时访问同一个后台接口（`api.tailscale.com`）；没装 Tailscale、你点了安装，或者你点了更新 Tailscale 时，会从 Tailscale 官网（`pkgs.tailscale.com`）下载它的安装包；检查更新时会访问 GitHub（`api.github.com` 和它的下载地址）。「自动下载更新」默认关着，不打开的话只有你点「检查更新」时才会连。

收发记录、排着要发的文件清单、分享列表、每分钟的流量和延迟记录、设置都只存在这台电脑的 `%LOCALAPPDATA%\TailOrbit\` 里，不会上传；管理接口的令牌也存在这里，用 Windows 加密过，只有你这个 Windows 账号解得开。剪贴板的内容和传给手机的声音不记录。查哪些端口连得进来，只是在本机读正在监听的端口、对应的程序和防火墙规则，不会去连谁。登录 Tailscale 是在你的默认浏览器里登，TailOrbit 只是把 Tailscale 给的登录网址交给浏览器，碰不到你的账号密码。

<details>
<summary>具体调了哪些命令</summary>

| 做什么 | 怎么做 |
| --- | --- |
| 列出能收文件的设备 | `tailscale file cp --targets`、`tailscale status --json` |
| 判断直连还是中继 | `tailscale ping` |
| 发文件、看收发进度 | `tailscale file cp`；进度来自 `tailscale debug watch-ipn` |
| 把收到的文件放进「下载」 | Windows 上收到的文件先留在 Tailscale 的收件箱里，平常由它自己的托盘程序（`tailscale-ipn`）取走；它没开着时，一批收完、以及 TailOrbit 启动时，用 `tailscale file get --conflict=rename "<下载文件夹>"` 取出来，同名的另起名字，不覆盖 |
| 等离线的设备上线 | 有排着的文件时每 15 秒看一次 `tailscale file cp --targets`，对方在线了照常用 `tailscale file cp` 发 |
| 连接、断开 | 连接是不带参数的 `tailscale up`（只把网络重新打开，不改设置），断开是 `tailscale down` |
| 登录、账号 | 没登录过、添加账号用 `tailscale login`；密钥过期后重新登录当前账号用 `tailscale debug localapi POST login-interactive`（在 Tailscale 自己的托盘程序里点登录，发的也是这个请求，设置都不动）。两种都会用默认浏览器打开 Tailscale 给的登录网址，等它连上。换账号是 `tailscale switch`，账号列表来自 `tailscale switch --list --json`，退出登录是 `tailscale logout` |
| 替 Tailscale 提醒 | TailOrbit 开着时每 10 秒读一次 `tailscale status --json`（状态、密钥到期时间、Tailscale 报的问题），再看 Tailscale 自己的托盘程序开没开着；开着就不弹 |
| 资源管理器右键「发送到」 | 安装时在你自己的「发送到」文件夹（`%APPDATA%\Microsoft\Windows\SendTo`）里放一个 `TailOrbit.lnk`，卸载时删掉；不写注册表、不用管理员权限。选中的文件跟在 `--send` 后面，开着的 TailOrbit 接过去弹出快捷面板 |
| 设备菜单里的「远程桌面」 | 用 Windows 自带的 `mstsc /v:<设备在 Tailscale 里的名字>` |
| 分享 | `tailscale serve`、`tailscale funnel`；文件由 TailOrbit 在 `127.0.0.1` 上开的小服务提供，Tailscale 只负责转发 |
| 网络检测、流量 | `tailscale netcheck`、`tailscale metrics print` |
| 流量和延迟记录 | TailOrbit 开着时每分钟读一次 `tailscale status --json`，正在通信的设备再 `tailscale ping` 一次，按天记在 `%LOCALAPPDATA%\TailOrbit\net\` 里；只往后加，不删也不上传 |
| DNS 检查 | `tailscale get` 看本机用不用 Tailscale 的 DNS，`tailscale status --json` 看 MagicDNS 开没开，再在本机解析一台在线设备的名字 |
| 哪些端口连得进来 | 在本机读正在监听的端口、对应的程序和 Windows 防火墙规则，挑出从 Tailscale 连得到的；`tailscale serve` 挂着的也列进来 |
| 管理接口 | 令牌用 Windows 的 DPAPI 加密，存在 `%LOCALAPPDATA%\TailOrbit\admin.key`；连上时用它读一次设备列表和令牌的到期时间，之后只在你点批准、关闭到期、删除或改名时访问 `api.tailscale.com` |
| 云盘 | `tailscale drive`；别的设备的共享通过 Tailscale 自带的 WebDAV（`100.100.100.100:8080`）读 |
| 开通云盘 | 用你的令牌读出访问规则，只加两条，写回前确认这期间没人改过，最后把令牌作废 |
| 出口节点和 Tailscale 开关 | `tailscale get`、`tailscale set`；推荐的出口节点来自 `tailscale exit-node suggest`；共享局域网是 `tailscale set --advertise-routes=<这台电脑所在的网段>`，同时带上现在的出口节点设置，不会顺带改掉它；没连管理接口时改名用 `tailscale set --hostname=` |
| 在不信任的网络上自动使用出口节点 | 网络变了时读一次 Windows 的网络列表，认出现在连的是哪个网络，信任的网络只记它的编号；开着这个功能时每 30 秒再用 `tailscale status --json` 看一眼。不在信任的网络上就 `tailscale set --exit-node=<你选的设备>`，回到信任的网络再用 `tailscale set --exit-node=` 关掉。只关它自己打开的；你自己改过出口节点，它就不再管 |
| 剪贴板互通 | `tailscale serve` 把 7443 端口上一个带随机码的地址转给本机的小服务，只放行你自己账号的设备 |
| 在手机上收听本机声音 | 转发和放行规则同剪贴板互通；有人在听时才用 Windows 的回环录音取系统正在放的声音，原样推给手机，不存盘；iPhone App 开着最粗的定位（3 公里精度）只是为了能在后台一直等着，位置不存、不发 |
| 读取 Mac 剪贴板、与 Mac 共享剪贴板 | 经 `ssh` 在 Mac 上跑 `pbpaste` 和一个小脚本；Windows 这边靠系统发来的“剪贴板变了”的通知。两边写进去时都带个记号，不会来回打转 |
| 设备菜单里的「网页」 | 设备在线时先试连几个常见的网页端口，菜单一打开就知道有没有 |
| 安装、更新 Tailscale | 从 `pkgs.tailscale.com` 下载对应处理器的 MSI，用 Windows 验证 Authenticode 签名并确认签名者是 Tailscale Inc.，再用 `msiexec` 安装 |
| 给桌面小组件用的本机接口 | 只听 `127.0.0.1:46214`，每次启动换一把随机令牌，存在 `%LOCALAPPDATA%\TailOrbit\island.json`；能列设备、发文件、看收发进度。小组件说要接手通知时，TailOrbit 的通知交给它显示，不再弹在屏幕右下角 |
| 检查更新 | 从 `api.github.com` 读最新发布的版本；安装包从 GitHub 下载到 `%LOCALAPPDATA%\TailOrbit\update\`，大小和 SHA-256 要和发布页上写的一样、版本号也对得上才装，装的时候不弹窗口运行它 |

</details>

### 其他

界面有简体中文、繁體中文、English、日本語、한국어、Deutsch、Français、Español 八种，默认跟着系统语言，在「设置」页可以换；分享出去的网页按访问者浏览器的语言显示。

这是个人项目，和 Tailscale 公司没有关系，也没有得到它的认可。Tailscale 和 Taildrop 是 Tailscale Inc. 的商标。代码用 [MIT 协议](LICENSE)；图标来自微软的 Fluent UI System Icons（同样是 MIT 协议），见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## English

TailOrbit is a small Windows app for [Tailscale](https://tailscale.com). It lays your devices out as a star map around this PC. Drag a file onto a device and it goes there over Taildrop.

The window has five pages along the top: Orbit, Share, Network, Drive and Settings. `Ctrl+1` to `Ctrl+5` switch between them.

### Sending and receiving

The map has three rings. Devices you reach directly sit on the inner ring, relayed ones (or ones not checked yet) on the middle ring, and offline ones on the outer ring. Each time the window opens, TailOrbit pings the online devices once, and any that turn out to be direct slide into the inner ring.

There are three ways to send:

- drag files into the window and drop them on a device;
- click a device and choose **Send files**;
- select files in File Explorer, press `Ctrl+Alt+A` (or right-click → **Send to** → **TailOrbit**; on Windows 11 it's under **Show more options**), then press the number shown next to a device.

Folders get zipped first. While a file is on its way, a progress ring runs around the device; hover it and click the × to cancel. The device turns green when the file arrives, and the line turns red if it fails.

Offline devices can receive too. Drop files on one (or click it and choose **Send files**, or press its number in the `Ctrl+Alt+A` panel) and they wait in line, shown as a small satellite circling the device. When the device comes back online, say when you open a closed laptop, TailOrbit sends them automatically: the satellite drops into the device and the usual send begins. TailOrbit only remembers where the files are and doesn't copy them, so whatever is there at that moment is what gets sent; if a file was moved or deleted, that batch is marked as failed. Changed your mind? Click the device and choose **Cancel sending**. Waiting files survive a restart, but this PC has to be on, with TailOrbit running, when the other device comes online.

When someone sends you a file, dots flow from their device into the middle. Received files show up at the bottom left: click one to find it in File Explorer, or drag it onto another device to pass it on. They land in Downloads: usually Tailscale's own tray app puts them there, and when it isn't running TailOrbit does it instead, without overwriting a file that has the same name (the new one becomes "name (1)"), and lets you know with a notification.

### Clicking a device

Click a device and a ring of buttons pops out around it: open a terminal (with the alias you gave it in `~/.ssh/config`), Remote Desktop (Windows devices only, and that device needs Remote Desktop turned on), send files, copy its DNS name, open its web page (only when it has a web port open), read its clipboard (Macs only), and **Alert me when offline**, which makes Windows notify you when that device drops off and when it comes back.

Click a device's name to copy its IP. Click this PC in the middle to open Downloads, where Tailscale puts the files you receive. Closing the window only hides it in the tray. Right-click the tray icon for a small menu: the **Connect to Tailscale** switch at the top, then **Open TailOrbit**, **Send files**, **Received files** (shows the last file you received), **Terminal** (point at it to pick a device), the two clipboard switches, and **Exit** at the bottom.

### Connection and accounts

TailOrbit can stand in for Tailscale's own tray icon:

- **Connect and disconnect**: the switch at the top of the tray menu, or the **Status** row on the left of the Settings page. While disconnected, the map and the Network page say **Disconnected**; click **Connect** to get back on, with all your settings as they were.
- **Sign in**: if this PC isn't signed in, or its key has expired, that switch becomes **Sign in to Tailscale** or **Sign in to Tailscale again**. Click it and Tailscale's sign-in page opens in your browser; once you've signed in, it connects by itself. Signing in again keeps the same account and all your settings.
- **Switch accounts**: click **Account** on the Settings page to see the accounts signed in on this PC, with a check next to the current one; click another to switch to it. You can also **Add account** (sign in to another account; if you don't finish, it switches back) or **Sign out**. Signing out takes this PC off your Tailscale network and stops shares, the shared clipboard and phone listening, so it asks for a second click.
- **Alerts**: when Tailscale's own tray app isn't running, TailOrbit shows its notifications for it: you need to sign in, this PC's key expires within 7 days (at most once a day), Tailscale has reported a problem for more than a minute (each one only once), or Tailscale was disconnected or its background service stopped. When its tray app is running, TailOrbit doesn't repeat them.

### Clipboard

The Settings page has two clipboard switches.

**Shared clipboard** is for your phone. Turn it on and a QR code pops up. Scan it with a phone signed in to Tailscale and you get a page showing this PC's clipboard: tap **Copy** to take it, or **Send my clipboard** to put the phone's text on the PC.

If scanning gets old, make two shortcuts in the iPhone's Shortcuts app and put them on Back Tap (Settings → Accessibility → Touch → Back Tap):

- Get the PC's clipboard: **Get Contents of URL** (the address from the QR code with `raw` added at the end) → **Copy to Clipboard**. When the PC's clipboard holds an image, that address has no text, so put an **If … has any value** in between, or your phone's clipboard gets wiped.
- Send to the PC: **Get Clipboard** → **Text** → **Get Contents of URL**, with Method set to POST and Request Body set to File, holding the text.

With the companion iPhone app (see **Listening on your phone**) you can skip the QR code and the shortcuts: the app stays connected to this PC in the background, so whatever you copy here, text or an image, lands on the iPhone's clipboard and you can paste it right away without opening the app. The other way, copy on the iPhone, open the app and tap **Paste**: iOS doesn't let apps read the clipboard in the background, so this takes one tap. Passwords stay on that iPhone only and clear themselves after two minutes.

**Sync clipboard with Mac** is for your Mac. Turn it on and this PC and the Mac act like one clipboard: copy on either, paste on both. Text and images (screenshots, say) come across; copied files don't. Images over 32 MB are skipped with a notification; send those with **Send files**. It connects with the alias for your Mac in `~/.ssh/config`, so Remote Login needs to be on, and nothing is installed on the Mac. The little laptop next to the switch lights up while it's connected. When the Mac sleeps the link drops; when it wakes, TailOrbit reconnects and passes along anything copied in between. If your iPhone and Mac share an Apple Account with Handoff on, whatever you copy on the iPhone comes through the Mac too.

If you'd rather not sync all the time, press `Win+Alt+V` whenever you want to pull the Mac's clipboard onto this PC.

The clipboard only travels between your own devices: only devices signed in to your Tailscale account can read it, nothing goes through Funnel or the internet, and nothing is written to disk. Passwords come along too; between the two computers they stay marked as passwords, so they don't end up in clipboard history. Tailscale access tokens are the exception: they're never passed to your Mac or phone.

### Listening on your phone

Turn on **Listen to this PC on your phone** on the Settings page and a QR code pops up too. Scan it with a phone signed in to Tailscale and tap the big round button: the phone, and the earbuds connected to it (AirPods, say), plays whatever this PC is playing. The sound isn't compressed and only a few dozen milliseconds are held back, a little more when the network gets bumpy; **Buffer** under the button shows how much right now. Bluetooth earbuds add their own delay on top.

The PC keeps playing through its own speakers. The stream is 16-bit stereo, about 190 KB a second, so an hour on mobile data is roughly 0.7 GB. As with the clipboard, only devices on your own Tailscale account can open it, and nothing goes through Funnel.

If you'd rather not scan and tap every time, there's a companion iPhone app in `ios/` (you build and install it yourself with a Mac; see **Build it yourself**). Open it once and allow location, and it waits in the background: while the phone is on Wi-Fi and your earbuds are in, whatever the PC plays comes through them automatically, and music already playing on the phone just gets quieter instead of stopping. Eight seconds after the PC goes quiet, it stops and lets go of the earbuds. Take the earbuds out or leave Wi-Fi and it disconnects, so it costs no battery or data. It shares the clipboard with the PC too; see **Clipboard**. If you swipe the app away or restart the phone, open it once more.

### Network

The Network page shows your devices on the left, each marked direct or relayed, with its latency and how fast it's transferring right now. Click a device for its card: the path it's taking, its latency over the last 24 hours (solid while direct, dashed while relayed), and its traffic for each of the last 30 days.

At the top on the right is anything that needs attention: problems Tailscale reports, shares that won't open, this PC's exit node or local network still waiting for approval, keys that expire within two weeks, and devices that have been offline for more than a week. Each one has an **Approve**, **Disable expiry** or **Delete** button right next to it; Delete asks for a second click.

Below that is the network check: UDP, IPv6, NAT type, the nearest relay and so on, plus whether DNS can resolve names on your tailnet and which ports on this PC other devices can connect to (click it to see the programs and ports). Windows Firewall doesn't block connections coming in over Tailscale by default; what keeps devices out is the access rules in your admin console, so this is what other devices can reach whenever those rules let them.

At the bottom is traffic for this session, today, the last 7 days or the last 30 days, split into direct and relayed. TailOrbit records each device's traffic and latency once a minute and keeps the records only on this PC, so **Today** and the longer ranges start from when you install this version. When a device you're using switches to a relay or its latency gets much higher than usual, you get a notification; turn off **Alert me when connections get worse** on the Settings page if you'd rather not.

**Approve**, **Disable expiry** and **Delete** need the Admin API: click **Admin API** on the Settings page, generate an access token in the Tailscale admin console when asked, and click Copy; TailOrbit picks it up and clears your clipboard. The token is stored encrypted on this PC and only sent to Tailscale; when it expires, connect again. To stop, click **Disconnect** and revoke the token on the Keys page of the admin console. You don't have to connect at all: the buttons then turn into **Go to admin console** so you can do it on the web.

### The other pages

- **Share**: drop a file or folder here to get a link and QR code that only your devices can open. If you need to, make it public on the internet for 15 minutes, an hour or a day; it closes itself when time is up. Anything you already serve with `tailscale serve` or `funnel` shows up here too, and turns red if the program behind it has stopped.
- **Drive**: Tailscale's Taildrive. If it isn't set up yet, click **Enable Drive**, generate an access token in the Tailscale admin console when asked, and click Copy. TailOrbit adds the two entries Taildrive needs to your access rules and revokes the token right away. If you'd rather not hand over a token, choose **Edit access controls manually**; the text to paste is already copied for you. Once it's on, drop a folder in to share it with your other devices, and open the folders they share.
- **Settings**: on the left, this computer's name, addresses, connection status, account and Tailscale version on Tailscale. Click the name to rename this PC; if it was renamed in the admin console before, that only works with the Admin API connected. If the shared clipboard or phone listening is on, the addresses saved on your phone change with the name, so it warns you first and renames on a second Enter. When a newer Tailscale is out, **Update Tailscale to …** appears below, and one click plus a Yes in the administrator prompt installs it. On the right, from top to bottom: the exit node (the one Tailscale recommends is marked **Recommended**), common Tailscale switches and the Admin API from **Network**, light, dark or follow Windows, **Start with Windows**, the interface language and **Alert me when connections get worse**, the two clipboard switches, listening on your phone, and checking for updates. One of the Tailscale switches, **Let other devices access the local network through this PC**, shares the local network this PC is on (your router or printer, say) with your other devices; it needs approving once in the admin console, and TailOrbit does that for you when the Admin API is connected. Set the exit node to **Use automatically on untrusted networks** and TailOrbit switches to the device you picked whenever you join a network like a café's or an airport's; mark your home and work networks as trusted and it turns the exit node off again when you're back on them.

### Install

You need Windows 10 (1903 or later) or Windows 11. It runs on the .NET Framework 4.8 that comes with Windows, so there's nothing else to install.

Download the latest `TailOrbit-Setup-<version>.exe` from [Releases](https://github.com/TinSB/TailOrbit/releases), double-click it and click **Install**. No admin rights needed: it goes into `%LOCALAPPDATA%\Programs\TailOrbit`, shows up in the Start menu and in File Explorer's **Send to** menu, and starts with Windows unless you turn that off.

If Tailscale isn't installed yet, the installer adds an **Also install Tailscale** option (on by default). After installing TailOrbit, it downloads the installer for your processor from the official Tailscale website, checks that it carries a valid signature from Tailscale Inc., and hands it to Windows to install (you'll see one administrator prompt), then opens Tailscale so you can sign in. If that step fails, TailOrbit still works, and the main window offers the same **Install Tailscale** button.

The installer isn't code-signed, so the first time you run it Windows may say "Windows protected your PC". Click **More info**, then **Run anyway**.

To update, click **Check for updates** on the Settings page, then **Update now**. TailOrbit downloads the new installer from GitHub, checks its size and SHA-256, replaces itself with the new version and reopens; your settings and history stay. Turn on **Download updates automatically** and it checks once a day, downloads new versions in the background and waits for you to click **Restart to update**. Downloading the new installer and running it yourself still works too.

### Uninstall

In **Settings → Apps**, find TailOrbit and click **Uninstall**. The shares, shared clipboard and phone listening it set up in Tailscale are taken down too, and its entries in the Start menu and **Send to** are removed. Your settings, transfer history and Admin API token are kept unless you turn on **Also delete settings and transfer history**. If you're done with the token, also revoke it on the Keys page of the admin console.

### Build it yourself

You can skip the installer and build it with the compiler that ships with Windows; no Visual Studio needed:

```powershell
git clone https://github.com/TinSB/TailOrbit.git
cd TailOrbit
powershell -ExecutionPolicy Bypass -File .\build.ps1
.\TailOrbit.exe
```

Run `TailOrbit.exe --tray` to start in the tray only; that's how **Start with Windows** launches it. Only one copy runs at a time, and launching it again just brings the open window to the front; `TailOrbit.exe --send <files…>` opens the quick panel with those files (that's how **Send to** launches it).

`.\release.ps1` builds `dist\TailOrbit-Setup-<version>.exe`. Add `--quiet` to the installer to install without any window (it then starts in the tray; add `--open` to open the main window instead); `TailOrbit.exe --uninstall --quiet` removes it the same way, and `--purge` also deletes settings and history.

The icon comes in a dark and a light version; the taskbar, tray and Start menu switch along with Windows light or dark mode. Both are drawn by `make-icon.ps1`: edit it and run it again to regenerate `TailOrbit.ico` and `TailOrbit-light.ico`.

The iPhone app lives in `ios/` and needs a Mac with Xcode and [XcodeGen](https://github.com/yonaskolb/XcodeGen). Create `ios/Local.xcconfig` with your signing team, the listening address from the Settings page (`//` starts a comment in xcconfig files, so write it as `https:/$()/...`) and your phone's device identifier; the top of `Config.xcconfig` shows the format. Then run `./mac.sh devinstall` on the Mac to build and install it on the phone (signing only works in the Mac's logged-in desktop session, not over SSH), or `./mac.sh sim` to try it in the Simulator. `make-icon.ps1 -Ios` draws the app icon too.

### Privacy

TailOrbit doesn't talk to any outside server; it leaves everything it can to Tailscale's own command line. There are four exceptions: Drive setup uses the token you give it to call Tailscale's admin API once; with the Admin API connected, TailOrbit checks the token once when you connect, and after that calls the same API (`api.tailscale.com`) only when you click **Approve**, **Disable expiry** or **Delete**, or rename this PC; if you choose to install Tailscale, or to update it, the installer is downloaded from the official Tailscale website (`pkgs.tailscale.com`); and checking for updates contacts GitHub (`api.github.com` and its download servers). **Download updates automatically** is off by default, so unless you turn it on, that only happens when you click **Check for updates**.

Your history, the list of files waiting to be sent, shares, the per-minute traffic and latency records, and settings stay in `%LOCALAPPDATA%\TailOrbit\` on this PC and are never uploaded. The Admin API token is kept there too, encrypted by Windows so only your Windows account can read it. Clipboard contents and the sound sent to your phone aren't recorded. Checking which ports other devices can reach only reads the listening ports, their programs and the firewall rules on this PC; it doesn't connect to anything. Signing in to Tailscale happens in your default browser: TailOrbit only hands it the sign-in address Tailscale gives it, and never sees your password.

<details>
<summary>What it runs under the hood</summary>

| What | How |
| --- | --- |
| List devices that can receive files | `tailscale file cp --targets`, `tailscale status --json` |
| Tell direct from relayed | `tailscale ping` |
| Send files and track progress | `tailscale file cp`; progress comes from `tailscale debug watch-ipn` |
| Putting received files in Downloads | on Windows, received files wait in Tailscale's inbox and are normally picked up by its own tray app (`tailscale-ipn`); when that isn't running, TailOrbit runs `tailscale file get --conflict=rename "<Downloads>"` after each batch arrives and when it starts, so a file with the same name gets a new name instead of being overwritten |
| Wait for an offline device | while files are waiting, checks `tailscale file cp --targets` every 15 seconds and sends with `tailscale file cp` once the device is online |
| Connect and disconnect | connecting runs `tailscale up` with no flags (it only turns the network back on and changes no settings); disconnecting runs `tailscale down` |
| Sign-in and accounts | signing in for the first time and adding an account run `tailscale login`; signing in again after the key expires runs `tailscale debug localapi POST login-interactive`, the same request Tailscale's own tray app sends when you sign in from it, which leaves your settings alone. Both open the sign-in address Tailscale gives in your default browser and wait until it's connected. Switching accounts runs `tailscale switch`, the account list comes from `tailscale switch --list --json`, and signing out runs `tailscale logout` |
| Alerts in place of Tailscale's | while TailOrbit runs, it reads `tailscale status --json` every 10 seconds (the state, when the key expires, and any problems Tailscale reports) and checks whether Tailscale's own tray app is running; if it is, TailOrbit stays quiet |
| **Send to** in File Explorer | installing puts a `TailOrbit.lnk` in your own Send to folder (`%APPDATA%\Microsoft\Windows\SendTo`), and uninstalling deletes it; nothing goes into the registry and no admin rights are needed. The selected files follow `--send`, and the running TailOrbit takes them and opens the quick panel |
| **Remote Desktop** in the device menu | runs Windows' own `mstsc /v:<the device's Tailscale name>` |
| Share | `tailscale serve`, `tailscale funnel`; files are served by a small TailOrbit server on `127.0.0.1`, and Tailscale only forwards to it |
| Network check and traffic | `tailscale netcheck`, `tailscale metrics print` |
| Traffic and latency history | while TailOrbit runs, reads `tailscale status --json` once a minute and runs `tailscale ping` once for each device that's talking, saving one file per day in `%LOCALAPPDATA%\TailOrbit\net\`; it only ever adds to them and never uploads them |
| DNS check | `tailscale get` to see whether this PC uses Tailscale DNS, `tailscale status --json` for MagicDNS, then resolves an online device's name on this PC |
| Which ports other devices can reach | reads the listening ports, their programs and the Windows Firewall rules on this PC, and keeps the ones reachable over Tailscale; anything you serve with `tailscale serve` is listed too |
| Admin API | the token is encrypted with Windows DPAPI and saved in `%LOCALAPPDATA%\TailOrbit\admin.key`; it reads the device list and the token's expiry once when you connect, and after that only calls `api.tailscale.com` when you approve, disable expiry, delete or rename |
| Drive | `tailscale drive`; other devices' shares are read through Tailscale's built-in WebDAV (`100.100.100.100:8080`) |
| Drive setup | reads your access rules with the token, adds two entries, checks that nobody changed them in the meantime before writing back, then revokes the token |
| Exit node and Tailscale switches | `tailscale get`, `tailscale set`; the recommended exit node comes from `tailscale exit-node suggest`; sharing the local network runs `tailscale set --advertise-routes=<this PC's subnet>` together with the current exit node setting, so that doesn't get changed along the way; without the Admin API, renaming runs `tailscale set --hostname=` |
| Use an exit node automatically on untrusted networks | reads the Windows network list when the network changes to tell which network you're on, and keeps only the IDs of trusted networks; while this is on, it also checks `tailscale status --json` every 30 seconds. On an untrusted network it runs `tailscale set --exit-node=<the device you picked>`, and `tailscale set --exit-node=` once you're back on a trusted one. It only turns off an exit node it turned on itself, and leaves things alone once you've changed the exit node yourself |
| Shared clipboard | `tailscale serve` forwards an address with a random code on port 7443 to the small local server, and only lets in devices on your own account |
| Listening on your phone | forwarded and limited to your own account the same way as the shared clipboard; only while someone is listening does it use Windows loopback recording to pick up what the PC is playing and pass it straight to the phone, without writing anything to disk; the iPhone app keeps the coarsest location updates (3 km) running only so it can wait in the background, and never stores or sends your location |
| Reading the Mac, syncing with the Mac | runs `pbpaste` and a small script on the Mac over `ssh`; on Windows it listens for the system's "clipboard changed" notice. Both sides tag what they write, so nothing bounces back and forth |
| "Web" in the device menu | tries a few common web ports while the device is online, so the menu knows right away |
| Installing and updating Tailscale | downloads the MSI for your processor from `pkgs.tailscale.com`, has Windows verify its Authenticode signature and checks the signer is Tailscale Inc., then installs it with `msiexec` |
| Local API for desktop widgets | listens only on `127.0.0.1:46214`, with a new random token each launch saved in `%LOCALAPPDATA%\TailOrbit\island.json`; lists devices, sends files, streams transfer progress. When a widget asks to take over notifications, TailOrbit's notifications show there instead of the corner of the screen |
| Updates | reads the latest release from `api.github.com`; the installer is downloaded from GitHub into `%LOCALAPPDATA%\TailOrbit\update\` and only used if its size and SHA-256 match the release page and its version number matches; it then runs without a window |

</details>

### Notes

The interface comes in Simplified Chinese, Traditional Chinese, English, Japanese, Korean, German, French and Spanish. It follows your Windows language and can be changed on the Settings page; shared web pages follow the visitor's browser language.

This is a personal project, not affiliated with or endorsed by Tailscale Inc. Tailscale and Taildrop are trademarks of Tailscale Inc. The code is under the [MIT License](LICENSE); the icons are Microsoft's Fluent UI System Icons (also MIT), see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
