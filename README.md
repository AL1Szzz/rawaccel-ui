# RawAccelUI — Raw Accel 的现代界面 + 调参测量工具链

> 给自己用的 Raw Accel 伴侣程序：一个能看懂的界面、一套只读诊断命令，以及把「调鼠标加速到底有没有用」变成可测量问题的分析脚本。
>
> 它**不是**外挂：不读游戏内存、不注入、不宏、不改游戏文件。驱动是上游官方签名的 `rawaccel.sys`，本项目只是它的配置客户端和测量工具。

中文界面 · 英文代码注释 · Windows · .NET 8

---

## 为什么做这个

官方 RawAccel GUI 能配曲线，但配完你无法回答任何一个问题：

- 这条曲线在我**真实甩枪**时到底生效了多少？
- 改了参数之后，我是真的变准了，还是今天状态好？
- 桌面点球的分数，和进游戏压枪的手感，有什么关系？

所以这个项目分三层：**驱动客户端**（写曲线）、**测量**（看你真实的手）、**判据**（怎么才算有证据）。

## 组成

| 目录 | 说明 |
|---|---|
| `RaCore/` | 类库。驱动协议（IOCTL + 内存布局）、加速曲线数学、真实手速统计、录像无关的会话规则。**曲线数学是上游 C++ 头文件的逐字节移植**，界面画的曲线就是驱动实际执行的映射。 |
| `RaProbe/` | 命令行诊断。默认只读；写驱动必须显式 `--yes`，且带校验→写→回读→不一致自动回滚。 |
| `RaUI/` | WPF 界面（深色、Fluent 风格、托盘常驻、任务栏图标）。 |
| `field/` | Node 分析脚本：CS2 录像解析、压枪手速采集判读、弹匣深度分析。 |
| `*.cmd` | 双击即用的入口（诊断、分析、状态查询），不要求你会命令行。 |

界面四页：**曲线与参数**（看图 + 手动调参）、**设备**（每只鼠标单独指定）、**瞄准测试**（点小球）、**我的方案**（保存/切换/撤回）、**设置**。

## 测量方法（这个项目真正的部分）

调参最容易犯的错，是拿一个会自我欺骗的指标下结论。这里几条判据都是踩过坑之后定下来的：

1. **闭环指标测不出增益改动。** 给鼠标更多加速，人会下意识少甩一点，最终点击位置不变。所以主判据是**开环**：第一下甩动的最远到达点 `max_extent`（>100% = 甩过头），它来不及被修正。
2. **路径重建要能自证。** 手速包是 counts，指针走的是像素，两者必须换算；换算系数 `k` 现在会写进日志——一轮的 `k` 偏离 1 很远，说明测的更多是重建而不是动作。
3. **同一份数据里不能混进"补枪"。** 打偏后对同一个球的立即补枪是短程移动，会把开环中位数往"很准"的方向拉，还会让散布看起来变小。现在距离分桶和开环用同一个剔除集合。
4. **A/B/A，并且用两侧 A 自己算噪声底。** 同配置两臂差多少，就是当晚能分辨的下限；比它小的差别一律判"测不出差别"。界面会自动识别 A→B→A，不会把"撤回基线"当成"这次改动的效果"。
5. **指标要过游戏机制这一关。** 例：靶子会死，所以"弹匣第 21–30 发命中率"量的其实是尸体；改用"一匣放倒几具身体"和"命中部位随弹匣深度的分布"（头份额随深度上升 = 没压住，四肢上升 = 压过头）。
6. **视角 ≠ 鼠标输入。** 录像里的俯仰角是「后坐力 + 你的下压」的净值，不能用它反推手速；手速要从驱动之后的原始输入包量。

### 一个真实结论

纵向加速曲线对压枪**结构性无效**：classic 模式增益是 `1 + accel·(v − 阈值)²/v`，在阈值处恰好等于 1。实测 60 秒压枪的纵向手速中位数只有 **0.38–0.49 counts/ms**，而阈值设在 2.5——整段压枪的增益恒为 1，把阈值降到 0.75 也只有 p90 提升 2.7%。压枪需要的是**与速度无关的恒定垂直倍率**，不是速度型加速。这类结论只能靠"量自己的手"得到，看曲线形状是看不出来的。

## 自检与质量闸门

不靠"看起来没问题"：

```powershell
app\RaProbe.exe --selftest      # 222 项离线断言：曲线数学、结构体尺寸、统计、方案库、模拟器…
node field\test.mjs             # 33 项分析数学：角度/俯仰符号、甩枪重建、弹匣切分、命中部位…
node field\check-cmd.mjs        # 守 *.cmd 的两类静默故障（UTF-8 中文字节、echo 里未转义的 >）
publish.cmd                     # 两道闸门：先跑 check-cmd，发布后再跑发布产物的 --selftest
```

`RaProbe --selftest` 全程不碰驱动、不改显示分辨率、不注入鼠标输入，所以可以在你打游戏时跑。界面改动用 `--dump-ui` / `--dump-flick` / `--dump-tray` **离屏渲染成 PNG** 检查，同样不抢焦点。

## 快速开始

前置：Windows 10/11、[.NET 8 SDK](https://dotnet.microsoft.com/download)、已安装官方 [Raw Accel 1.7.x](https://github.com/rawaccel/rawaccel) 驱动（本项目不安装驱动）。

```powershell
git clone <本仓库地址> RawAccelUI
cd RawAccelUI
publish.cmd                    # 产物固定输出到 app\
app\RaUI.exe
```

只读诊断：

```powershell
app\RaProbe.exe                        # 结构体尺寸、驱动版本、当前生效配置的完整 dump
app\RaProbe.exe --curve-name           # 现在驱动里跑的是哪套设置（报名字，不报哈希）
app\RaProbe.exe --devices              # 枚举鼠标（HID 实例 ID 与驱动匹配用的 id）
app\RaProbe.exe --poll-jitter 60       # 采 60 秒原始输入包，看投递是否规律、掉包损失多少增益
app\RaProbe.exe --apply-settings curves\V-yx1.25.json      # 只读：显示会改哪些字段
app\RaProbe.exe --apply-settings curves\V-yx1.25.json --yes # 写入（校验→写→回读→不一致回滚）
```

录像分析需要 Node（`field/package.json` 里只有一个依赖 `@laihoe/demoparser2`）：

```powershell
cd field && npm install
node field\field.mjs --list            # 找可读的录像
node field\field.mjs --latest          # 分析最近一局，写 report.txt / report.json / shots.csv
node field\spray-bursts.mjs --last 3   # 按弹匣深度比较几场靶局
```

## 数据放在哪

全部在 `%LOCALAPPDATA%\RawAccelUI\`，重装程序不会丢：

```
logs\            每轮小球测试的完整日志 + index.csv（含当轮冻结的配置快照）
presets\         我的方案（同名 .txt 是人话说明）；history\ 是覆盖/删除前的自动备份
patches\         写入审计
config-backups\  settings.json 的时间戳备份（保留最近 60 份）
field\           录像报告、弹串对比
raui.ui.json     界面偏好（与驱动配置分开，绝不会污染官方 settings.json）
```

驱动配置文件沿用官方的 `settings.json` 路径，所以官方 GUI 和本程序可以共存。

## 已知限制（诚实清单）

- 曲线数学只对 **classic** 模式做过与官方 wrapper 的逐字节差分；jump / natural / synchronous / power / lut 是移植的，但没有同等强度的验证。
- 「我的方案」保存的是鼠标解析到的那组参数（未列出的设备落回第一组 profile）；**每只鼠标绑定不同方案**这种用法目前要在「设备」页手工指定。
- 开环指标依赖原始输入采样，采样设备选错或系统休眠过，该轮会写明不可用而不是硬算。
- CS2 客户端本地录像（POV）没有视角和弹着点，只能给命中/爆头/击杀；要弹道几何得用服务端（TV）录像。
- 这是个人工具：没有安装器、没有签名、没有自动更新，界面文案只有中文。

## 许可

暂未添加 LICENSE 文件（默认保留所有权利）。曲线数学与驱动协议部分来自上游 [rawaccel](https://github.com/rawaccel/rawaccel)，遵循其原始许可。

## 致谢

- [rawaccel](https://github.com/rawaccel/rawaccel) — 驱动与曲线定义
- [@laihoe/demoparser2](https://www.npmjs.com/package/@laihoe/demoparser2) — CS2 (Source 2) 录像解析
