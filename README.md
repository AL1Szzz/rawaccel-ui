# RawAccelUI — Raw Accel 的现代界面

> 给自己用的 Raw Accel 伴侣程序：一套看得懂的中文界面、开机自启、任务栏常驻、点小球练手感，以及随时一键切回去的「我的方案」。
>
> 它**不是**外挂：不读游戏内存、不注入、不写宏、不改游戏文件。驱动是上游官方签名的 `rawaccel.sys`，本项目只是它的配置客户端。

[![CI](https://github.com/AL1Szzz/rawaccel-ui/actions/workflows/ci.yml/badge.svg)](https://github.com/AL1Szzz/rawaccel-ui/actions/workflows/ci.yml)

中文界面 · 英文代码注释 · Windows · .NET 8 (WPF)

---

## 为什么做这个

官方 GUI 能配曲线，但日常用起来会卡在四件事上：

- 参数名全是英文术语（`input offset`、`cap mode`、`yx_output_dpi_ratio`），改一个要查半天。
- 驱动本身不保存设置，开机不写入就等于没加速；官方的 `writer.exe` 快捷方式又不好管。
- 想试两套参数，只能反复手填，还容易忘了当前跑的是哪一套。
- 想知道"改了有没有感觉"，只能进游戏打两枪。

所以这个程序只做五页，每页一件事：

| 页面 | 干什么 |
|---|---|
| **曲线与参数** | 左边画实际生效的灵敏度/增益曲线，右边所有参数用人话列出（带悬停解释），底部一行告诉你阈值等于多少 cm/s、多少 °/s。 |
| **设备** | 每只鼠标单独绑定一组参数；不绑定的鼠标落回第一组。 |
| **瞄准测试** | 无边框全屏点小球。点掉的球跳到别处，可限时或限球数，结束自动还原分辨率，留最近 80 次成绩。同一页还有**训练场**（网格速击 / 六目标 / 跟枪）和 **360° 灵敏度校准**，都跑在同一个分辨率设置下。 |
| **我的方案** | 把当前参数存成一个有中文名字的方案，一键切换、一键撤回上次应用；基线受保护不被覆盖。 |
| **设置** | 开机自启（可替代官方 writer.exe）、托盘常驻、全屏测试的分辨率。 |

关窗口不等于退出：托盘一直在，左键收放窗口，右键是"写入驱动 / 打开数据目录 / 退出"。

## 组成

| 目录 | 说明 |
|---|---|
| `RaCore/` | 类库。驱动协议（IOCTL + 内存布局）、加速曲线数学、方案库、显示模式切换。**曲线数学是上游 C++ 头文件的逐字节移植**，界面画的就是驱动实际执行的映射。 |
| `RaUI/` | WPF 界面（深色、Fluent 风格、托盘常驻、自带图标）。单文件发布。 |
| `RaProbe/` | 命令行诊断。默认只读；写驱动必须显式 `--yes`，且带校验→写→回读→不一致自动回滚。 |
| `*.cmd` | 双击即用的入口（`publish.cmd` 发布、`曲线状态.cmd` 看驱动里现在跑的是什么、`回报率.cmd` 数系统真正投递的鼠标包）。 |

## 写驱动的安全网

改鼠标加速最坏的情况是"应用完鼠标不能用了"，所以写入路径上没有捷径：

1. 先跑 `Json.Validate`：NaN、无穷、超过 1e5 的数、拼错的枚举、指向不存在 profile 的设备，全部在写之前挡掉。范围检查与上游 `rawaccel-validate.hpp` 逐条对齐（输出 DPI / 各向倍率 / 域权重 / lpNorm / 指数 / 封顶与阈值的先后 / LUT 点数 / 时间上下限），因为**驱动拒绝写入或把位移乘成 0 的那些值，回读一致也照样查不出来**——它确实按我们给的字节存好了，只是那套字节让鼠标不动。whole 模式下驱动不看 Y 轴参数，这里也不看，免得拦掉官方 GUI 愿意写的文件。
2. 记下驱动当前的字节，写入，等过驱动的 1 秒节流，**回读并逐字节比对**；不一致就抛错。
3. 失败时把先前那份字节写回去，再回读确认，只有确认一致才敢说"已恢复"。
4. 成功后才存文件（`settings.json` 原子写 + `.bak` + `config-backups\` 时间戳备份，保留最近 60 份）。驱动写成功和文件存成功是两件事，状态栏分开说。
5. 「我的方案」换曲线走的是同一条路径，并且只替换 profile 的**内容**、保留它的**名字**——设备是按名字引用 profile 的，改名会把绑定悬空。
6. 应用过程中整个编辑区禁用，避免边写边改。
7. 开机自启的命令里**钉住当前设置文件的绝对路径**（不钉的话，登录那次会重新猜一遍路径，猜错就是往另一个文件写）；并且登录时的自动写入会拒绝"界面里是我们自己造的默认文档"这种情况——文件读不出来、驱动又是空的时，写下去等于换了一套你没选过的曲线，界面会直接说没写。

## 自检与质量闸门

不靠"看起来没问题"：

```powershell
publish.cmd                     # 发布 + 用发布出来的二进制跑离线断言，不绿就别用这个构建
app\RaProbe.exe --selftest      # 179 项离线断言
```

每次 push / PR 会在 GitHub Actions 上跑同一套（`dotnet build -warnaserror` + 发布 RaProbe + `--selftest`），见 `.github/workflows/ci.yml`。只在 Windows 上跑：RaUI 是 WPF，RaCore 直接 p/invoke `CreateFileW` / `DeviceIoControl`，Linux 上没有可跑的东西。

`--selftest` 全程不碰驱动、不改分辨率、不注入鼠标输入，所以可以在你打游戏时跑。它守的都是真出过问题的地方：结构体尺寸与驱动头文件一致、blob 往返逐字节相同、非 ASCII 设备名能过线、方案名的哈希必须等于驱动回报的哈希、成绩历史里一行坏数据不能拖垮整张表、阈值必须落在曲线自己的低速基线上（带垂直倍率时，参照 1.0 会误报"0 就开始加速"）、上面每条驱动侧范围检查都真的拦得住、以及**这台机器正在用的 settings.json 必须仍然通过校验**（不然以后每次收紧都只是把你的曲线挡在门外）。

界面改动用离屏渲染检查，同样不抢焦点：

```powershell
app\RaUI.exe --dump-ui "$env:TEMP\page.png" --page 0   # 五页逐页渲染成 PNG
app\RaUI.exe --dump-flick "$env:TEMP\flick.png"        # 全屏测试窗（假指针位置）
app\RaUI.exe --dump-drill "$env:TEMP\d.png" --kind 0   # 训练场：0 网格速击 / 1 六目标 / 2 跟枪
app\RaUI.exe --dump-calib "$env:TEMP\c.png"            # 校准转盘
app\RaUI.exe --calib-live 5                           # 真的开 5 秒转盘，报原始输入收到了多少包
app\RaUI.exe --dump-tray                               # 托盘提示与右键菜单
app\RaUI.exe --autostart-check                         # 自启项往返：装→查→原样还原
```

## 快速开始

前置：Windows 10/11、[.NET 8 SDK](https://dotnet.microsoft.com/download)、已安装官方 [Raw Accel 1.7.x](https://github.com/RawAccelOfficial/rawaccel) 驱动（本项目不安装驱动）。

```powershell
git clone <本仓库地址> RawAccelUI
cd RawAccelUI
publish.cmd          # 产物固定输出到 app\
app\RaUI.exe
```

只读诊断：

```powershell
app\RaProbe.exe                 # 结构体尺寸、驱动版本、当前生效配置的完整 dump
app\RaProbe.exe --curve-name    # 驱动里现在跑的是哪套（报名字，不报哈希）
app\RaProbe.exe --devices       # 枚举鼠标（与驱动匹配用的 HID 实例 ID）
app\RaProbe.exe --hz 15         # 数 Windows 真正投递给应用的鼠标包（raw input，按设备分）
app\RaProbe.exe --hz 20 --hz-fg # 同上，但听的是前台可见窗口（游戏用的那一路），会弹一个小窗口
app\RaProbe.exe --display       # 系统提供的显示模式列表（只列不改）
app\RaProbe.exe --apply-settings settings.json          # 只读：显示会改哪些字段
app\RaProbe.exe --apply-settings settings.json --yes    # 写入（走上面那套安全网）
```

## 数据放在哪

全部在 `%LOCALAPPDATA%\RawAccelUI\`，重装程序不会丢：

```
test-history.csv    小球测试成绩（一行一次，中文表头，任何编辑器能开）
presets\            我的方案（同名 .txt 是人话说明）；history\ 是覆盖/删除前的自动备份
config-backups\     settings.json 的时间戳备份（保留最近 60 份）
raui.ui.json        界面偏好（与驱动配置分开，绝不会污染官方 settings.json）
settings-path.txt   可选：只写一行路径。程序猜不到官方 settings.json 装在哪时才用它
```

驱动配置文件沿用官方的 `settings.json`：程序会在常见安装位置里找它（找不到就用自己数据目录里的一份，并在界面上写明），所以官方 GUI 和本程序改的是同一个文件。命令行想指定别的文件用 `--settings <路径>`。

## 已知限制（诚实清单）

- 曲线数学只对 **classic** 模式做过与官方 wrapper 的逐字节差分；jump / natural / synchronous / power / lut 是移植的，但没有同等强度的验证。
- 「我的方案」保存的是鼠标解析到的那组参数（未列出的设备落回第一组 profile）；**每只鼠标绑定不同方案**要在「设备」页手工指定。
- 点小球和训练场都是桌面指针测试：按像素摆球，不换算游戏角度，所以分数只能当"手感有没有变"的粗指标，不等于进游戏的成绩。
- **360° 校准测的是手，不是游戏**。转盘的转角 = 计数 × (0.022 × 灵敏度)，也就是游戏相机的那套映射，所以"转一圈"必然在你转满 360° 的地方发生，它没法反过来告诉你游戏里的灵敏度是多少。它真正给出的是两个东西：一是**这段距离有多少厘米**（原始计数 ÷ 你填的 DPI，这个是实测的），二是**想要 30cm 转一圈，游戏里该填多少**（反向换算，和曲线页的 cm/360 用的是同一个公式）。DPI 填错，厘米数就跟着错。
- 没有安装器、没有代码签名、没有自动更新，界面文案只有中文。首次运行 Windows 可能提示不受信任的应用。
- **回报率不能用浏览器页面或 `WM_MOUSEMOVE` 类工具判**：Windows 在 mouclass/RIT 层就会把鼠标包合并，网页上的 pointerrawupdate 和老式 Mouse Rate 数的是合并后的投递，2000 和 4000 在它眼里可以一模一样。`--hz`（或双击 `回报率.cmd`）数的是 raw input 包，并且同时报每包位移——包数低而每包位移大，就是被合过，不是传感器降频。要分辨只有一种办法：改着测，2000 跑一次、4000 跑一次，看包/秒跟不跟着翻倍。
- **但 `--hz` 默认听的是后台路径**（消息专用窗口 + `RIDEV_INPUTSINK`）。前台可见窗口和后台 sink 的投递节律可能不同，所以"后台数到 125"**不能直接推成"游戏也只拿到 125"**——要看游戏那一侧，用 `--hz-fg`（或双击 `回报率-前台.cmd`）听前台窗口那一路，两个数对比着下结论。

## 许可

本项目采用 **MIT**（见 [`LICENSE`](LICENSE)）。

曲线数学、驱动协议结构体与参数范围检查移植自上游 [rawaccel](https://github.com/RawAccelOfficial/rawaccel)（同为 MIT）。上游的版权声明与许可声明已按要求保留在 `LICENSE` 末尾，不做删改。

## 致谢

- [rawaccel](https://github.com/RawAccelOfficial/rawaccel) — 驱动与曲线定义（MIT）
